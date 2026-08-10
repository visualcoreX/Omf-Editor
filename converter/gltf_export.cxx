#include "gltf_export.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <map>
#include <sstream>
#include <vector>

#include "xr_ogf.h"
#include "xr_ogf_format.h"
#include "xr_bone.h"
#include "xr_skl_motion.h"
#include "xr_geom_buf.h"
#include "xr_influence.h"
#include "xr_matrix.h"
#include "xr_quaternion.h"

using namespace xray_re;

namespace {

// glTF component types
enum {
	CT_UBYTE	= 5121,
	CT_USHORT	= 5123,
	CT_FLOAT	= 5126,
};

// bufferView targets
enum {
	TARGET_NONE	= 0,
	TARGET_ARRAY	= 34962,
	TARGET_ELEMENT	= 34963,
};

std::string fmt_float(float v)
{
	if (!(v == v) || v > 3.0e38f || v < -3.0e38f)	// NaN/inf are not valid JSON
		v = 0;
	char buf[40];
	std::snprintf(buf, sizeof(buf), "%.9g", double(v));
	return std::string(buf);
}

std::string json_string(const std::string& s)
{
	std::string out("\"");
	for (std::string::const_iterator it = s.begin(), end = s.end(); it != end; ++it) {
		unsigned char c = static_cast<unsigned char>(*it);
		switch (c) {
		case '"': out += "\\\""; break;
		case '\\': out += "\\\\"; break;
		case '\b': out += "\\b"; break;
		case '\f': out += "\\f"; break;
		case '\n': out += "\\n"; break;
		case '\r': out += "\\r"; break;
		case '\t': out += "\\t"; break;
		default:
			if (c < 0x20 || c >= 0x80) {
				// keep the JSON strictly 7-bit: bone names come from cp1251 data
				char buf[8];
				std::snprintf(buf, sizeof(buf), "\\u%04x", c);
				out += buf;
			} else {
				out += char(c);
			}
			break;
		}
	}
	out += '"';
	return out;
}

// S = diag(1, 1, -1) conjugation: turns an X-Ray (left handed) affine transform
// into its right handed counterpart. M' = S*M*S negates every element with
// exactly one index on the Z axis.
inline void flip_z(fmatrix& m)
{
	m._13 = -m._13;
	m._23 = -m._23;
	m._31 = -m._31;
	m._32 = -m._32;
	m._43 = -m._43;
}

// Row-vector convention again: p' = p*M, so a component of the result picks a
// column of the matrix.
inline void transform_point(const fmatrix& m, float x, float y, float z, float out[3])
{
	out[0] = x*m._11 + y*m._21 + z*m._31 + m._41;
	out[1] = x*m._12 + y*m._22 + z*m._32 + m._42;
	out[2] = x*m._13 + y*m._23 + z*m._33 + m._43;
}

inline void transform_dir(const fmatrix& m, float x, float y, float z, float out[3])
{
	out[0] = x*m._11 + y*m._21 + z*m._31;
	out[1] = x*m._12 + y*m._22 + z*m._32;
	out[2] = x*m._13 + y*m._23 + z*m._33;
}

// The rotation of a matrix as a quaternion, in the element order the library
// uses. Its own conversion kills the process through xr_assert on anything it
// does not take for a clean rotation - a preview is never worth taking the
// editor down for, so degenerate input yields no rotation at all instead.
void quat_from_matrix(const fmatrix& m, fquaternion& q)
{
	float trace = m._11 + m._22 + m._33;
	if (trace > 0) {
		float s = std::sqrt(trace + 1.f);
		if (!(s > 1e-6f)) {
			q.identity();
			return;
		}
		q.w = s*0.5f;
		s = 0.5f/s;
		q.x = s*(m._32 - m._23);
		q.y = s*(m._13 - m._31);
		q.z = s*(m._21 - m._12);
	} else {
		int i = 0;
		if (m._22 > m._11)
			i = 1;
		if (m._33 > m.m[i][i])
			i = 2;
		int j = (i + 1)%3, k = (j + 1)%3;
		float s = std::sqrt(m.m[i][i] - m.m[j][j] - m.m[k][k] + 1.f);
		if (!(s > 1e-6f)) {
			q.identity();
			return;
		}
		q.xyz[i] = s*0.5f;
		s = 0.5f/s;
		q.w = s*(m.m[k][j] - m.m[j][k]);
		q.xyz[j] = s*(m.m[j][i] + m.m[i][j]);
		q.xyz[k] = s*(m.m[k][i] + m.m[i][k]);
	}
}

// Shortest way round between two orientations. The library slerp feeds acos a
// dot product that rounding can push past 1, and the NaN that comes out of it
// travels all the way into the assert above.
void quat_slerp(const fquaternion& a, const fquaternion& b, float t, fquaternion& out)
{
	float dot = a.dot_product(b);
	float sign = 1.f;
	if (dot < 0) {			// the far side of the same orientation
		dot = -dot;
		sign = -1.f;
	}
	if (dot > 1.f)
		dot = 1.f;

	float k0 = 1.f - t, k1 = t;
	if (dot < 0.9995f) {		// too close together and the sines cancel out
		float omega = std::acos(dot);
		float sin_omega = std::sin(omega);
		if (sin_omega > 1e-6f) {
			k0 = std::sin(k0*omega)/sin_omega;
			k1 = std::sin(k1*omega)/sin_omega;
		}
	}
	k1 *= sign;

	out.set(k0*a.x + k1*b.x, k0*a.y + k1*b.y, k0*a.z + k1*b.z, k0*a.w + k1*b.w);
	float len = out.magnitude();
	if (len > 1e-8f)
		out.div(len);
	else
		out.identity();
}

// X-Ray matrices are row-vector (v*M), glTF ones are column-vector (M*v), so the
// glTF matrix is the transpose - which is exactly the X-Ray element order read
// as a column-major array. The quaternion needs conjugating for the same reason.
void matrix_to_trs(const fmatrix& m, float t[3], float q[4])
{
	t[0] = m._41;
	t[1] = m._42;
	t[2] = m._43;

	fquaternion quat;
	quat_from_matrix(m, quat);
	float len = std::sqrt(quat.x*quat.x + quat.y*quat.y + quat.z*quat.z + quat.w*quat.w);
	if (len < 1e-8f) {
		q[0] = q[1] = q[2] = 0;
		q[3] = 1;
		return;
	}
	q[0] = -quat.x/len;
	q[1] = -quat.y/len;
	q[2] = -quat.z/len;
	q[3] = quat.w/len;
}

class gltf_builder {
public:
			gltf_builder();

	// appends `count` elements to the binary chunk and returns the accessor index
	int		add_accessor(const void* data, size_t count, size_t elem_size,
					int comp_type, const char* type, int target,
					const std::string& extra = std::string());

	int		num_accessors() const { return m_num_accessors; }
	const std::vector<uint8_t>& bin() const { return m_bin; }
	std::string	buffer_views() const { return m_views.str(); }
	std::string	accessors() const { return m_accessors.str(); }

private:
	std::vector<uint8_t>	m_bin;
	std::ostringstream	m_views;
	std::ostringstream	m_accessors;
	int			m_num_views;
	int			m_num_accessors;
};

gltf_builder::gltf_builder(): m_num_views(0), m_num_accessors(0) {}

int gltf_builder::add_accessor(const void* data, size_t count, size_t elem_size,
		int comp_type, const char* type, int target, const std::string& extra)
{
	while (m_bin.size() & 3)
		m_bin.push_back(0);
	size_t offset = m_bin.size();
	size_t length = count*elem_size;
	m_bin.resize(offset + length);
	if (length)
		std::memcpy(&m_bin[offset], data, length);

	if (m_num_views)
		m_views << ',';
	m_views << "{\"buffer\":0,\"byteOffset\":" << offset << ",\"byteLength\":" << length;
	if (target != TARGET_NONE)
		m_views << ",\"target\":" << target;
	m_views << '}';

	if (m_num_accessors)
		m_accessors << ',';
	m_accessors << "{\"bufferView\":" << m_num_views
		<< ",\"componentType\":" << comp_type
		<< ",\"count\":" << count
		<< ",\"type\":\"" << type << '"';
	if (!extra.empty())
		m_accessors << ',' << extra;
	m_accessors << '}';

	++m_num_views;
	return m_num_accessors++;
}

////////////////////////////////////////////////////////////////////////////////

struct gltf_primitive {
	int	position;
	int	normal;
	int	texcoord;
	int	joints;
	int	weights;
	int	indices;
	int	material;
};

// The file name alone: the preview and its textures share a folder, so that is
// what the glTF refers to them by.
std::string file_name(const std::string& path)
{
	std::string::size_type slash = path.find_last_of("\\/");
	return slash == std::string::npos ? path : path.substr(slash + 1);
}

struct bone_node {
	std::string		name;
	int			parent;
	std::vector<int>	children;
	fmatrix			local;		// right handed bind pose, relative to parent
	fmatrix			world;		// right handed bind pose, model space
	fmatrix			world_i;	// inverse of the above
};

// Every OGF that actually carries geometry: either the children of a hierarchical
// model or the model itself.
void collect_visuals(const xr_ogf& ogf, std::vector<const xr_ogf*>& out)
{
	const std::vector<xr_ogf*>& children = ogf.children();
	if (children.empty()) {
		if (ogf.vb().size() && ogf.ib().size())
			out.push_back(&ogf);
		return;
	}
	for (std::vector<xr_ogf*>::const_iterator it = children.begin(), end = children.end();
			it != end; ++it) {
		collect_visuals(**it, out);
	}
}

bool build_skeleton(const xr_ogf& ogf, std::vector<bone_node>& bones, std::string& error)
{
	const xr_bone_vec& src = ogf.bones();
	if (src.empty()) {
		error = "model has no bones";
		return false;
	}

	std::map<std::string, int> by_name;
	bones.resize(src.size());
	for (size_t i = 0; i != src.size(); ++i) {
		if (src[i] == 0) {
			error = "model has a hole in its bone list";
			return false;
		}
		bones[i].name = src[i]->name();
		bones[i].parent = -1;
		by_name[bones[i].name] = int(i);
	}

	for (size_t i = 0; i != src.size(); ++i) {
		const std::string& parent_name = src[i]->parent_name();
		if (parent_name.empty())
			continue;
		std::map<std::string, int>::const_iterator it = by_name.find(parent_name);
		if (it == by_name.end() || it->second == int(i))
			continue;
		bones[i].parent = it->second;
		bones[it->second].children.push_back(int(i));
	}

	// bind pose, converted to the right handed basis
	for (size_t i = 0; i != src.size(); ++i) {
		fmatrix& local = bones[i].local;
		local.set_xyz_i(src[i]->bind_rotate());
		local.c.set(src[i]->bind_offset());
		flip_z(local);
	}

	// world transforms need parents resolved first - the OGF bone list is already
	// topologically sorted, but do not rely on it
	std::vector<bool> done(bones.size(), false);
	for (size_t pass = 0; pass != bones.size() + 1; ++pass) {
		bool progress = false, pending = false;
		for (size_t i = 0; i != bones.size(); ++i) {
			if (done[i])
				continue;
			int parent = bones[i].parent;
			if (parent < 0) {
				bones[i].world.set(bones[i].local);
			} else if (done[parent]) {
				bones[i].world.mul_43(bones[parent].world, bones[i].local);
			} else {
				pending = true;
				continue;
			}
			done[i] = true;
			progress = true;
		}
		if (!pending)
			break;
		if (!progress) {
			error = "bone hierarchy contains a loop";
			return false;
		}
	}

	for (size_t i = 0; i != bones.size(); ++i) {
		if (!bones[i].world.can_invert_43()) {
			error = "bone \"" + bones[i].name + "\" has a degenerate bind pose";
			return false;
		}
		bones[i].world_i.invert_43(bones[i].world);
	}
	return true;
}

// The pose a single bone motion holds at `frame`, in the X-Ray basis. Motions
// read from an OMF end up as envelopes of step keys - one per frame, no shape
// between them - so a fractional frame is blended here: position linearly, and
// orientation through the quaternions, the way the engine itself does it.
void evaluate_bone(const xr_bone_motion& bm, float frame, fmatrix& xform)
{
	float floor_frame = std::floor(frame);
	float alpha = frame - floor_frame;

	fvector3 t0, r0;
	bm.evaluate(floor_frame/OGF4_MOTION_FPS, t0, r0);
	if (alpha < 1e-4f) {
		xform.set_xyz_i(r0);
		xform.c.set(t0);
		return;
	}

	fvector3 t1, r1;
	bm.evaluate((floor_frame + 1.f)/OGF4_MOTION_FPS, t1, r1);

	fmatrix m0, m1;
	m0.set_xyz_i(r0);
	m1.set_xyz_i(r1);

	fquaternion q0, q1, q;
	quat_from_matrix(m0, q0);
	quat_from_matrix(m1, q1);
	quat_slerp(q0, q1, alpha, q);

	fvector3 t;
	t.lerp(t0, t1, alpha);
	xform.mk_xform(q, t);
}

// Parent-relative transform of every bone at `frame`, in the right handed basis.
// Bones the motion says nothing about keep their bind pose. `out_world`, when
// given, receives the model space transforms the skinning needs.
void evaluate_pose(const std::vector<bone_node>& bones,
		const std::vector<const xr_bone_motion*>& bone_motion,
		float frame, std::vector<fmatrix>& local, std::vector<fmatrix>* out_world = 0)
{
	// world transforms are needed so that world-oriented bones can be turned
	// back into plain parent-relative node transforms
	std::vector<fmatrix> world(bones.size()), world_i(bones.size());

	local.resize(bones.size());
	for (size_t i = 0; i != bones.size(); ++i) {
		const xr_bone_motion* bm = bone_motion[i];
		if (bm != 0) {
			evaluate_bone(*bm, frame, local[i]);
			flip_z(local[i]);
		} else {
			local[i].set(bones[i].local);
		}

		int parent = bones[i].parent;
		if (bm != 0 && parent >= 0 && (bm->flags() & xr_bone_motion::BMF_WORLD_ORIENT)) {
			// the key holds a model space orientation: rebuild the world
			// transform the engine would use, then make it relative again
			fmatrix xform;
			xform.mul_43(world[parent], local[i]);
			xform.i.set(local[i].i);
			xform.j.set(local[i].j);
			xform.k.set(local[i].k);
			world[i].set(xform);
			local[i].mul_43(world_i[parent], world[i]);
		} else if (parent >= 0) {
			world[i].mul_43(world[parent], local[i]);
		} else {
			world[i].set(local[i]);
		}

		if (world[i].can_invert_43())
			world_i[i].invert_43(world[i]);
		else
			world_i[i].identity();
	}

	if (out_world != 0)
		out_world->swap(world);
}

// Linear blend skinning matrix of a single vertex: bind pose out, animated pose
// in, weighted over the bones the vertex hangs on.
bool blend_vertex_xform(const finfluence& infl, const std::vector<fmatrix>& skin_xform,
		fmatrix& out, std::string& error)
{
	float sum = 0;
	for (size_t j = 0; j != infl.size(); ++j) {
		if (infl[j].bone >= skin_xform.size()) {
			error = "vertex references a bone outside the skeleton";
			return false;
		}
		sum += infl[j].weight;
	}
	if (infl.size() == 0 || !(sum > 1e-6f)) {
		// a weightless vertex follows its first bone, or stays put
		if (infl.size() != 0)
			out.set(skin_xform[infl[0].bone]);
		else
			out.identity();
		return true;
	}

	std::memset(&out, 0, sizeof(out));
	for (size_t j = 0; j != infl.size(); ++j) {
		float w = infl[j].weight/sum;
		const fmatrix& m = skin_xform[infl[j].bone];
		for (int k = 0; k != 16; ++k)
			out.__array[k] += w*m.__array[k];
	}
	return true;
}

} // anonymous namespace

////////////////////////////////////////////////////////////////////////////////

bool ogf_export_glb(const xr_ogf& ogf, const xr_skl_motion* motion, float frame,
		const ogf_texture_map& textures, const char* out_path, std::string& error)
{
	error.clear();

	std::vector<bone_node> bones;
	if (!build_skeleton(ogf, bones, error))
		return false;

	std::vector<const xr_ogf*> visuals;
	collect_visuals(ogf, visuals);
	if (visuals.empty()) {
		error = "model has no renderable geometry";
		return false;
	}

	// ---- motion ------------------------------------------------------------
	// the bone motions lined up with the model skeleton, empty without a motion
	std::vector<const xr_bone_motion*> bone_motion;
	int num_frames = 1;
	if (motion != 0) {
		bone_motion.assign(bones.size(), static_cast<const xr_bone_motion*>(0));
		const xr_bone_motion_vec& bone_motions = motion->bone_motions();
		std::map<std::string, const xr_bone_motion*> by_name;
		bool unnamed = true;
		for (size_t i = 0; i != bone_motions.size(); ++i) {
			if (bone_motions[i] == 0)
				continue;
			by_name[bone_motions[i]->name()] = bone_motions[i];
			if (!bone_motions[i]->name().empty())
				unnamed = false;
		}
		size_t matched = 0;
		for (size_t i = 0; i != bones.size(); ++i) {
			std::map<std::string, const xr_bone_motion*>::const_iterator it =
					by_name.find(bones[i].name);
			if (it != by_name.end()) {
				bone_motion[i] = it->second;
				++matched;
			}
		}
		// An OMF that holds its bones as bare ids gives their motions no names
		// to match on. There the id is the place in the skeleton, so the bones
		// are taken in order - but only when the names told us nothing at all,
		// or a motion made for another skeleton would be pulled onto this one.
		if (matched == 0 && unnamed && bone_motions.size() >= bones.size()) {
			for (size_t i = 0; i != bones.size(); ++i)
				bone_motion[i] = bone_motions[i];
		}

		num_frames = motion->frame_end() - motion->frame_start();
		if (num_frames < 1)
			num_frames = 1;
		if (frame > float(num_frames - 1))
			frame = float(num_frames - 1);
	} else {
		frame = -1;	// nothing to freeze without a motion
	}

	// What every joint node gets as its own transform: the bind pose normally,
	// the requested frame when the editor is scrubbing through a motion.
	std::vector<fmatrix> node_local(bones.size());
	for (size_t i = 0; i != bones.size(); ++i)
		node_local[i].set(bones[i].local);

	// A frozen frame is baked into the vertices instead of being left to the
	// skin: the glTF importer of the viewer only skins while an animation is
	// playing, so a posed skeleton on its own would still draw the bind pose.
	bool bake = (frame >= 0);
	std::vector<fmatrix> skin_xform;
	if (bake) {
		std::vector<fmatrix> world;
		evaluate_pose(bones, bone_motion, frame, node_local, &world);
		skin_xform.resize(bones.size());
		for (size_t i = 0; i != bones.size(); ++i)
			skin_xform[i].mul_43(world[i], bones[i].world_i);
	}

	gltf_builder b;
	std::vector<gltf_primitive> primitives;
	bool skinned = false;

	// The png of every texture actually used, in the order the materials refer
	// to them. Material 0 is the plain grey one for anything left untextured.
	std::vector<std::string> images;
	std::map<std::string, int> image_index;

	// ---- geometry ----------------------------------------------------------
	for (size_t v = 0; v != visuals.size(); ++v) {
		const xr_vbuf& vb = visuals[v]->vb();
		const xr_ibuf& ib = visuals[v]->ib();
		size_t num_verts = vb.size(), num_indices = ib.size();
		if (!num_verts || num_indices < 3 || !vb.has_points())
			continue;
		if (num_verts > 0xffff) {
			// OGF index buffers are 16 bit, so this cannot happen for valid files
			error = "vertex buffer is too large";
			return false;
		}

		// the skinning matrix of every vertex, only while baking a frozen frame
		std::vector<fmatrix> vertex_xform;
		if (bake && vb.has_influences()) {
			vertex_xform.resize(num_verts);
			for (size_t i = 0; i != num_verts; ++i) {
				if (!blend_vertex_xform(vb.w(i), skin_xform, vertex_xform[i], error))
					return false;
			}
		}

		std::vector<float> positions(num_verts*3);
		for (size_t i = 0; i != num_verts; ++i) {
			const fvector3& p = vb.p(i);
			if (vertex_xform.empty()) {
				positions[i*3 + 0] = p.x;
				positions[i*3 + 1] = p.y;
				positions[i*3 + 2] = -p.z;
			} else {
				transform_point(vertex_xform[i], p.x, p.y, -p.z, &positions[i*3]);
			}
		}

		fvector3 vmin, vmax;
		vmin.set(positions[0], positions[1], positions[2]);
		vmax.set(vmin);
		for (size_t i = 0; i != num_verts; ++i) {
			for (int c = 0; c != 3; ++c) {
				float value = positions[i*3 + c];
				if (value < vmin[c]) vmin[c] = value;
				if (value > vmax[c]) vmax[c] = value;
			}
		}

		std::ostringstream bounds;
		bounds << "\"min\":[" << fmt_float(vmin.x) << ',' << fmt_float(vmin.y) << ','
			<< fmt_float(vmin.z) << "],\"max\":[" << fmt_float(vmax.x) << ','
			<< fmt_float(vmax.y) << ',' << fmt_float(vmax.z) << ']';

		gltf_primitive prim;
		prim.normal = prim.texcoord = prim.joints = prim.weights = -1;
		prim.material = 0;

		const std::string& texture = visuals[v]->texture();
		if (!texture.empty()) {
			ogf_texture_map::const_iterator png = textures.find(texture);
			if (png != textures.end() && !png->second.empty()) {
				std::map<std::string, int>::const_iterator known =
						image_index.find(png->second);
				if (known != image_index.end()) {
					prim.material = known->second;
				} else {
					images.push_back(file_name(png->second));
					prim.material = int(images.size());	// 0 is the plain one
					image_index[png->second] = prim.material;
				}
			}
		}
		prim.position = b.add_accessor(&positions[0], num_verts, 12, CT_FLOAT, "VEC3",
				TARGET_ARRAY, bounds.str());

		if (vb.has_normals()) {
			std::vector<float> normals(num_verts*3);
			for (size_t i = 0; i != num_verts; ++i) {
				const fvector3& n = vb.n(i);
				float v[3] = { n.x, n.y, -n.z };
				if (!vertex_xform.empty())
					transform_dir(vertex_xform[i], v[0], v[1], v[2], v);
				float len = std::sqrt(v[0]*v[0] + v[1]*v[1] + v[2]*v[2]);
				if (len < 1e-8f)
					len = 1.f;
				normals[i*3 + 0] = v[0]/len;
				normals[i*3 + 1] = v[1]/len;
				normals[i*3 + 2] = v[2]/len;
			}
			prim.normal = b.add_accessor(&normals[0], num_verts, 12, CT_FLOAT, "VEC3",
					TARGET_ARRAY);
		}

		if (vb.has_texcoords()) {
			std::vector<float> uvs(num_verts*2);
			for (size_t i = 0; i != num_verts; ++i) {
				uvs[i*2 + 0] = vb.tc(i).x;
				uvs[i*2 + 1] = vb.tc(i).y;
			}
			prim.texcoord = b.add_accessor(&uvs[0], num_verts, 8, CT_FLOAT, "VEC2",
					TARGET_ARRAY);
		}

		// a baked frame carries no skin: the pose is already in the positions
		if (vb.has_influences() && !bake) {
			std::vector<uint16_t> joints(num_verts*4, 0);
			std::vector<float> weights(num_verts*4, 0.f);
			for (size_t i = 0; i != num_verts; ++i) {
				const finfluence& infl = vb.w(i);
				float sum = 0;
				size_t n = std::min<size_t>(infl.size(), 4);
				for (size_t j = 0; j != n; ++j) {
					uint32_t bone = infl[j].bone;
					if (bone >= bones.size()) {
						error = "vertex references a bone outside the skeleton";
						return false;
					}
					joints[i*4 + j] = uint16_t(bone);
					weights[i*4 + j] = infl[j].weight;
					sum += infl[j].weight;
				}
				if (sum > 1e-6f) {
					for (size_t j = 0; j != n; ++j)
						weights[i*4 + j] /= sum;
				} else {
					weights[i*4] = 1.f;
				}
			}
			prim.joints = b.add_accessor(&joints[0], num_verts, 8, CT_USHORT, "VEC4",
					TARGET_ARRAY);
			prim.weights = b.add_accessor(&weights[0], num_verts, 16, CT_FLOAT, "VEC4",
					TARGET_ARRAY);
			skinned = true;
		}

		// mirroring Z reverses the winding order
		size_t num_tris = num_indices/3;
		std::vector<uint16_t> indices(num_tris*3);
		for (size_t i = 0; i != num_tris; ++i) {
			indices[i*3 + 0] = ib[i*3 + 0];
			indices[i*3 + 1] = ib[i*3 + 2];
			indices[i*3 + 2] = ib[i*3 + 1];
		}
		prim.indices = b.add_accessor(&indices[0], num_tris*3, 2, CT_USHORT, "SCALAR",
				TARGET_ELEMENT);

		primitives.push_back(prim);
	}

	if (primitives.empty()) {
		error = "model has no renderable geometry";
		return false;
	}

	// ---- skin --------------------------------------------------------------
	int inverse_bind = -1;
	if (skinned) {
		std::vector<float> matrices(bones.size()*16);
		for (size_t i = 0; i != bones.size(); ++i)
			std::memcpy(&matrices[i*16], &bones[i].world_i._11, 16*sizeof(float));
		inverse_bind = b.add_accessor(&matrices[0], bones.size(), 64, CT_FLOAT, "MAT4",
				TARGET_NONE);
	}

	// ---- animation ---------------------------------------------------------
	std::ostringstream samplers, channels;
	int num_samplers = 0, num_channels = 0;
	std::string animation_name;

	if (motion != 0) {
		// a frozen frame is already baked into the geometry above
		if (!bake) {
			float speed = motion->speed();
			if (!(speed > 1e-3f))
				speed = 1.f;

			std::vector<float> times(num_frames);
			for (int i = 0; i != num_frames; ++i)
				times[i] = float(i)/(OGF4_MOTION_FPS*speed);

			std::ostringstream time_bounds;
			time_bounds << "\"min\":[" << fmt_float(times.front()) << "],\"max\":["
				<< fmt_float(times.back()) << ']';
			int input = b.add_accessor(&times[0], num_frames, 4, CT_FLOAT, "SCALAR",
					TARGET_NONE, time_bounds.str());

			std::vector<std::vector<float> > translations(bones.size());
			std::vector<std::vector<float> > rotations(bones.size());
			for (size_t i = 0; i != bones.size(); ++i) {
				if (bone_motion[i] == 0)
					continue;
				translations[i].resize(size_t(num_frames)*3);
				rotations[i].resize(size_t(num_frames)*4);
			}

			std::vector<fmatrix> pose(bones.size());
			for (int f = 0; f != num_frames; ++f) {
				evaluate_pose(bones, bone_motion, float(f), pose);
				for (size_t i = 0; i != bones.size(); ++i) {
					if (bone_motion[i] == 0)
						continue;
					float t[3], q[4];
					matrix_to_trs(pose[i], t, q);
					std::memcpy(&translations[i][size_t(f)*3], t, sizeof(t));
					std::memcpy(&rotations[i][size_t(f)*4], q, sizeof(q));
				}
			}

			for (size_t i = 0; i != bones.size(); ++i) {
				if (bone_motion[i] == 0)
					continue;
				int t_acc = b.add_accessor(&translations[i][0], num_frames, 12, CT_FLOAT,
						"VEC3", TARGET_NONE);
				int r_acc = b.add_accessor(&rotations[i][0], num_frames, 16, CT_FLOAT,
						"VEC4", TARGET_NONE);
				for (int k = 0; k != 2; ++k) {
					if (num_samplers)
						samplers << ',';
					samplers << "{\"input\":" << input << ",\"interpolation\":\"LINEAR\""
						<< ",\"output\":" << (k ? r_acc : t_acc) << '}';
					if (num_channels)
						channels << ',';
					channels << "{\"sampler\":" << num_samplers << ",\"target\":{\"node\":"
						<< i << ",\"path\":\"" << (k ? "rotation" : "translation")
						<< "\"}}";
					++num_samplers;
					++num_channels;
				}
			}

			animation_name = motion->name();
			if (animation_name.empty())
				animation_name = "motion";
		}
	}

	// ---- JSON --------------------------------------------------------------
	std::ostringstream js;
	js << "{\"asset\":{\"version\":\"2.0\",\"generator\":\"OMF Editor viewport\"}";
	js << ",\"scene\":0";

	int mesh_node = int(bones.size());
	js << ",\"scenes\":[{\"nodes\":[";
	bool first = true;
	for (size_t i = 0; i != bones.size(); ++i) {
		if (bones[i].parent >= 0)
			continue;
		if (!first)
			js << ',';
		js << i;
		first = false;
	}
	if (!first)
		js << ',';
	js << mesh_node << "]}]";

	js << ",\"nodes\":[";
	for (size_t i = 0; i != bones.size(); ++i) {
		float t[3], q[4];
		matrix_to_trs(node_local[i], t, q);
		if (i)
			js << ',';
		js << "{\"name\":" << json_string(bones[i].name)
			<< ",\"translation\":[" << fmt_float(t[0]) << ',' << fmt_float(t[1])
			<< ',' << fmt_float(t[2]) << ']'
			<< ",\"rotation\":[" << fmt_float(q[0]) << ',' << fmt_float(q[1]) << ','
			<< fmt_float(q[2]) << ',' << fmt_float(q[3]) << ']';
		if (!bones[i].children.empty()) {
			js << ",\"children\":[";
			for (size_t c = 0; c != bones[i].children.size(); ++c) {
				if (c)
					js << ',';
				js << bones[i].children[c];
			}
			js << ']';
		}
		js << '}';
	}
	js << ",{\"name\":\"mesh\",\"mesh\":0";
	if (skinned)
		js << ",\"skin\":0";
	js << "}]";

	js << ",\"meshes\":[{\"name\":\"model\",\"primitives\":[";
	for (size_t i = 0; i != primitives.size(); ++i) {
		const gltf_primitive& p = primitives[i];
		if (i)
			js << ',';
		js << "{\"attributes\":{\"POSITION\":" << p.position;
		if (p.normal >= 0)
			js << ",\"NORMAL\":" << p.normal;
		if (p.texcoord >= 0)
			js << ",\"TEXCOORD_0\":" << p.texcoord;
		if (p.joints >= 0)
			js << ",\"JOINTS_0\":" << p.joints << ",\"WEIGHTS_0\":" << p.weights;
		js << "},\"indices\":" << p.indices << ",\"material\":" << p.material << ",\"mode\":4}";
	}
	js << "]}]";

	if (skinned) {
		js << ",\"skins\":[{\"inverseBindMatrices\":" << inverse_bind << ",\"joints\":[";
		for (size_t i = 0; i != bones.size(); ++i) {
			if (i)
				js << ',';
			js << i;
		}
		js << "]}]";
	}

	js << ",\"materials\":[{\"name\":\"model\",\"doubleSided\":true,"
		"\"pbrMetallicRoughness\":{\"baseColorFactor\":[0.75,0.75,0.75,1],"
		"\"metallicFactor\":0,\"roughnessFactor\":0.6}}";
	for (size_t i = 0; i != images.size(); ++i) {
		// alpha stays masked rather than blended: X-Ray uses it to cut shapes
		// out of flat quads, and blending them needs a sorted draw order
		js << ",{\"name\":" << json_string(images[i])
			<< ",\"doubleSided\":true,\"alphaMode\":\"MASK\",\"alphaCutoff\":0.5,"
			"\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":" << i
			<< "},\"metallicFactor\":0,\"roughnessFactor\":0.8}}";
	}
	js << ']';

	if (!images.empty()) {
		js << ",\"images\":[";
		for (size_t i = 0; i != images.size(); ++i) {
			if (i)
				js << ',';
			js << "{\"uri\":" << json_string(images[i]) << '}';
		}
		js << "],\"samplers\":[{\"magFilter\":9729,\"minFilter\":9987,"
			"\"wrapS\":10497,\"wrapT\":10497}],\"textures\":[";
		for (size_t i = 0; i != images.size(); ++i) {
			if (i)
				js << ',';
			js << "{\"sampler\":0,\"source\":" << i << '}';
		}
		js << ']';
	}

	if (num_channels) {
		js << ",\"animations\":[{\"name\":" << json_string(animation_name)
			<< ",\"samplers\":[" << samplers.str() << "],\"channels\":["
			<< channels.str() << "]}]";
	}

	const std::vector<uint8_t>& bin = b.bin();
	size_t bin_length = (bin.size() + 3) & ~size_t(3);
	js << ",\"buffers\":[{\"byteLength\":" << bin_length << "}]";
	js << ",\"bufferViews\":[" << b.buffer_views() << ']';
	js << ",\"accessors\":[" << b.accessors() << ']';
	js << '}';

	std::string json = js.str();
	while (json.size() & 3)
		json += ' ';

	// ---- GLB container -----------------------------------------------------
	std::ofstream f(out_path, std::ios::binary | std::ios::trunc);
	if (!f) {
		error = "can't create ";
		error += out_path;
		return false;
	}

	uint32_t header[3] = {
		0x46546c67,	// "glTF"
		2,
		uint32_t(12 + 8 + json.size() + 8 + bin_length)
	};
	f.write(reinterpret_cast<const char*>(header), sizeof(header));

	uint32_t json_chunk[2] = { uint32_t(json.size()), 0x4e4f534a };	// "JSON"
	f.write(reinterpret_cast<const char*>(json_chunk), sizeof(json_chunk));
	f.write(json.data(), std::streamsize(json.size()));

	uint32_t bin_chunk[2] = { uint32_t(bin_length), 0x004e4942 };	// "BIN\0"
	f.write(reinterpret_cast<const char*>(bin_chunk), sizeof(bin_chunk));
	if (!bin.empty())
		f.write(reinterpret_cast<const char*>(&bin[0]), std::streamsize(bin.size()));
	static const char padding[4] = { 0, 0, 0, 0 };
	if (bin_length > bin.size())
		f.write(padding, std::streamsize(bin_length - bin.size()));

	f.close();
	if (!f) {
		error = "can't write ";
		error += out_path;
		return false;
	}
	return true;
}
