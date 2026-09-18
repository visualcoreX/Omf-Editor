// Reading motions out of SDK files (.skl, .skls) for the editor. They keep
// their keys as plain envelopes, while an OMF stores them one stream per bone of
// the model - so the motions are sampled frame by frame here and packed into
// exactly the layout xr_ogf_v4::bone_motion_io::import reads back. The keys stay
// 32 bit floats (KPF_FLOAT) rather than being squeezed into the stock 16/8 bit.

#include <cmath>
#include <cstring>
#include <string>
#include <vector>

#include "xr_file_system.h"
#include "xr_object.h"
#include "xr_ogf_format.h"
#include "xr_vector3.h"
#include "xr_matrix.h"
#include "xr_quaternion.h"
#include "xr_skl_motion.h"

using namespace xray_re;

namespace {

enum {
	SKL_OK			= 0,
	SKL_BAD_ARGUMENT	= -1,
	SKL_LOAD_FAILED		= -2,
	SKL_UNKNOWN_MOTION	= -3,
	SKL_NO_BONES		= -4,
};

std::string g_skl_error;
xr_skl_motion_vec g_motions;

void clear_motions()
{
	for (xr_skl_motion_vec_it it = g_motions.begin(), end = g_motions.end(); it != end; ++it)
		delete *it;
	g_motions.clear();
}

int copy_string(const std::string& value, char* buffer, int size)
{
	if (buffer == 0 || size <= 0)
		return int(value.size());
	int n = int(value.size());
	if (n > size - 1)
		n = size - 1;
	if (n > 0)
		std::memcpy(buffer, value.data(), size_t(n));
	buffer[n] = '\0';
	return n;
}

// With keep_empty the split is positional: one entry per separator, empty ones
// included. Bones of an OMF that holds them as bare ids arrive that way, and
// dropping them would shift every stream that follows.
void split_names(const char* text, std::vector<std::string>& out, bool keep_empty = false)
{
	std::string current;
	for (const char* p = text; ; ++p) {
		if (*p == '\n' || *p == '\0') {
			if (keep_empty || !current.empty())
				out.push_back(current);
			current.clear();
			if (*p == '\0')
				break;
		} else if (*p != '\r') {
			current += *p;
		}
	}
}

// The bone in a given place of the motion, for bones that have no name to be
// looked up by. Their id is that place.
const xr_bone_motion* bone_motion_at(const xr_skl_motion& motion, size_t index)
{
	const xr_bone_motion_vec& bone_motions = motion.bone_motions();
	return index < bone_motions.size() ? bone_motions[index] : 0;
}

// ---- writing -----------------------------------------------------------

struct blob {
	std::vector<uint8_t>	bytes;

	void	u8(unsigned value) { bytes.push_back(uint8_t(value)); }
	void	u32(uint32_t value)
	{
		for (int i = 0; i != 4; ++i)
			bytes.push_back(uint8_t((value >> (i*8)) & 0xff));
	}
	void	i16(int value)
	{
		bytes.push_back(uint8_t(value & 0xff));
		bytes.push_back(uint8_t((value >> 8) & 0xff));
	}
	void	i8(int value) { bytes.push_back(uint8_t(value & 0xff)); }
	void	f32(float value)
	{
		uint32_t raw;
		std::memcpy(&raw, &value, 4);
		u32(raw);
	}
};

int16_t quantize_q(float value)
{
	int q = int(value*32767.f + (value < 0 ? -0.5f : 0.5f));
	if (q > 32767)
		q = 32767;
	if (q < -32767)
		q = -32767;
	return int16_t(q);
}

// The reader turns a quantized quaternion into a matrix and pulls xyz_i euler
// angles out of it, so the way back is that same road reversed.
void euler_to_quat(const fvector3& r, fquaternion& q)
{
	fmatrix m;
	m.set_xyz_i(r);

	float trace = m._11 + m._22 + m._33;
	if (trace > 0) {
		float s = std::sqrt(trace + 1.f);
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

// One bone stream: every frame sampled, then written as the reader expects -
// quantized the way stock game files are, or with full_precision as the plain
// floats of KPF_FLOAT keys.
void write_bone_motion(blob& out, const xr_bone_motion* bm, int num_frames, bool full_precision)
{
	std::vector<fvector3> positions;
	std::vector<fquaternion> rotations;
	positions.resize(size_t(num_frames));
	rotations.resize(size_t(num_frames));

	for (int f = 0; f != num_frames; ++f) {
		fvector3 t, r;
		if (bm != 0) {
			bm->evaluate(float(f)/OGF4_MOTION_FPS, t, r);
		} else {
			t.set(0, 0, 0);
			r.set(0, 0, 0);
		}
		positions[size_t(f)].set(t);
		euler_to_quat(r, rotations[size_t(f)]);
	}

	// a bone that holds still gets a single key, which is what the format is
	// shaped for and what keeps a motion the same size it was
	bool same_rotation = true, same_position = true;
	for (int f = 1; f != num_frames; ++f) {
		const fquaternion& a = rotations[0];
		const fquaternion& b = rotations[size_t(f)];
		if (full_precision) {
			if (std::fabs(a.x - b.x) > 1e-7f || std::fabs(a.y - b.y) > 1e-7f ||
					std::fabs(a.z - b.z) > 1e-7f || std::fabs(a.w - b.w) > 1e-7f)
				same_rotation = false;
		} else if (quantize_q(a.x) != quantize_q(b.x) || quantize_q(a.y) != quantize_q(b.y) ||
				quantize_q(a.z) != quantize_q(b.z) || quantize_q(a.w) != quantize_q(b.w)) {
			same_rotation = false;
		}
		if (!positions[0].similar(positions[size_t(f)], 1e-6f))
			same_position = false;
		if (!same_rotation && !same_position)
			break;
	}

	unsigned flags = 0;
	if (same_rotation)
		flags |= KPF_R_ABSENT;
	if (!same_position)
		flags |= KPF_T_PRESENT;
	if (full_precision)
		flags |= KPF_FLOAT;
	out.u8(flags);

	if (full_precision) {
		// the keys as the SDK file has them, 32 bits a component
		if (!same_rotation)
			out.u32(0);		// crc of the keys, the loader skips it
		for (int f = 0, n = same_rotation ? 1 : num_frames; f != n; ++f) {
			const fquaternion& q = rotations[size_t(f)];
			out.f32(q.x);
			out.f32(q.y);
			out.f32(q.z);
			out.f32(q.w);
		}
		if (same_position) {
			out.f32(positions[0].x);
			out.f32(positions[0].y);
			out.f32(positions[0].z);
		} else {
			out.u32(0);		// crc again
			for (int f = 0; f != num_frames; ++f) {
				out.f32(positions[size_t(f)].x);
				out.f32(positions[size_t(f)].y);
				out.f32(positions[size_t(f)].z);
			}
		}
		return;
	}

	if (same_rotation) {
		const fquaternion& q = rotations[0];
		out.i16(quantize_q(q.x));
		out.i16(quantize_q(q.y));
		out.i16(quantize_q(q.z));
		out.i16(quantize_q(q.w));
	} else {
		out.u32(0);		// crc of the keys, the loader skips it
		for (int f = 0; f != num_frames; ++f) {
			const fquaternion& q = rotations[size_t(f)];
			out.i16(quantize_q(q.x));
			out.i16(quantize_q(q.y));
			out.i16(quantize_q(q.z));
			out.i16(quantize_q(q.w));
		}
	}

	if (!same_position) {
		fvector3 vmin(positions[0]), vmax(positions[0]);
		for (int f = 1; f != num_frames; ++f) {
			vmin.min(positions[size_t(f)]);
			vmax.max(positions[size_t(f)]);
		}

		// 8 bit keys over the range the bone actually travels, the same as the
		// game files use - the high quality variant only exists in newer builds
		fvector3 scale;
		for (int c = 0; c != 3; ++c) {
			float span = vmax[c] - vmin[c];
			scale[c] = span > 1e-8f ? span/127.f : 1.f;
		}

		out.u32(0);		// crc again
		for (int f = 0; f != num_frames; ++f) {
			for (int c = 0; c != 3; ++c) {
				float value = (positions[size_t(f)][c] - vmin[c])/scale[c];
				int q = int(value + 0.5f);
				if (q > 127)
					q = 127;
				if (q < -128)
					q = -128;
				out.i8(q);
			}
		}
		out.f32(scale.x);
		out.f32(scale.y);
		out.f32(scale.z);
		out.f32(vmin.x);
		out.f32(vmin.y);
		out.f32(vmin.z);
	} else {
		out.f32(positions[0].x);
		out.f32(positions[0].y);
		out.f32(positions[0].z);
	}
}

const xr_bone_motion* find_bone_motion(const xr_skl_motion& motion, const std::string& name)
{
	const xr_bone_motion_vec& bone_motions = motion.bone_motions();
	for (size_t i = 0; i != bone_motions.size(); ++i) {
		if (bone_motions[i] && bone_motions[i]->name() == name)
			return bone_motions[i];
	}
	return 0;
}

} // anonymous namespace

extern "C" {

// Loads every motion of a .skl (one) or .skls (many). Returns their number.
_declspec(dllexport) int SklOpen(const char* path)
{
	g_skl_error.clear();
	clear_motions();

	if (path == 0 || path[0] == '\0') {
		g_skl_error = "no path given";
		return SKL_BAD_ARGUMENT;
	}

	static bool fs_ready = false;
	if (!fs_ready) {
		xr_file_system::instance().initialize(0, 0);
		fs_ready = true;
	}

	std::string name(path);
	bool many = name.size() > 5 && (name.compare(name.size() - 5, 5, ".skls") == 0 ||
			name.compare(name.size() - 5, 5, ".SKLS") == 0);

	try {
		if (many) {
			xr_object object;
			if (!object.load_skls(path)) {
				g_skl_error = "can't load ";
				g_skl_error += path;
				return SKL_LOAD_FAILED;
			}
			// taken over rather than copied: the motions own their envelopes
			// through bare pointers, and the object deletes them on its way out
			g_motions.swap(object.motions());
		} else {
			xr_skl_motion* motion = new xr_skl_motion;
			if (!motion->load_skl(path)) {
				delete motion;
				g_skl_error = "can't load ";
				g_skl_error += path;
				return SKL_LOAD_FAILED;
			}
			g_motions.push_back(motion);
		}
	} catch (...) {
		clear_motions();
		g_skl_error = "unhandled error while reading ";
		g_skl_error += path;
		return SKL_LOAD_FAILED;
	}

	return int(g_motions.size());
}

_declspec(dllexport) int SklGetLastError(char* buffer, int size)
{
	return copy_string(g_skl_error, buffer, size);
}

_declspec(dllexport) int SklGetCount()
{
	return int(g_motions.size());
}

// The skeleton the motions were made for, in their own order. An editor with no
// OMF open has nothing else to build a bone list from.
_declspec(dllexport) int SklGetBoneCount()
{
	if (g_motions.empty())
		return 0;
	return int(g_motions.front()->bone_motions().size());
}

_declspec(dllexport) int SklGetBoneName(int index, char* buffer, int size)
{
	if (g_motions.empty())
		return copy_string(std::string(), buffer, size);
	const xr_bone_motion_vec& bones = g_motions.front()->bone_motions();
	if (index < 0 || index >= int(bones.size()) || bones[size_t(index)] == 0)
		return copy_string(std::string(), buffer, size);
	return copy_string(bones[size_t(index)]->name(), buffer, size);
}

_declspec(dllexport) int SklGetName(int index, char* buffer, int size)
{
	if (index < 0 || index >= int(g_motions.size()))
		return copy_string(std::string(), buffer, size);
	return copy_string(g_motions[size_t(index)]->name(), buffer, size);
}

// The motion parameters an OMF keeps next to the keys.
_declspec(dllexport) int SklGetParams(int index, float* speed, float* accrue, float* falloff,
		float* power, int* flags, int* bone_or_part, int* frames)
{
	if (index < 0 || index >= int(g_motions.size()))
		return SKL_UNKNOWN_MOTION;

	const xr_skl_motion* motion = g_motions[size_t(index)];
	int num_frames = motion->frame_end() - motion->frame_start();
	if (num_frames < 1)
		num_frames = 1;

	if (speed) *speed = motion->speed();
	if (accrue) *accrue = motion->accrue();
	if (falloff) *falloff = motion->falloff();
	if (power) *power = motion->power();
	if (flags) *flags = int(motion->flags());
	if (bone_or_part) *bone_or_part = int(motion->bone_or_part());
	if (frames) *frames = num_frames;
	return SKL_OK;
}

// Packs the motion into an OMF motion blob for the given bones, which have to
// be listed in the order of the target OMF, one per line. Pass a null buffer to
// ask for the size first.
_declspec(dllexport) int SklGetData(int index, const char* bone_names, unsigned char* buffer, int size)
{
	g_skl_error.clear();
	if (index < 0 || index >= int(g_motions.size()))
		return SKL_UNKNOWN_MOTION;
	if (bone_names == 0)
		return SKL_BAD_ARGUMENT;

	std::vector<std::string> bones;
	split_names(bone_names, bones, true);
	if (bones.empty() || (bones.size() == 1 && bones[0].empty())) {
		g_skl_error = "no bones given";
		return SKL_NO_BONES;
	}

	const xr_skl_motion* motion = g_motions[size_t(index)];
	int num_frames = motion->frame_end() - motion->frame_start();
	if (num_frames < 1)
		num_frames = 1;

	blob out;
	out.u32(uint32_t(num_frames));
	for (size_t i = 0; i != bones.size(); ++i) {
		const xr_bone_motion* bm = bones[i].empty() ?
				bone_motion_at(*motion, i) : find_bone_motion(*motion, bones[i]);
		// an SDK file holds its keys as floats, and they go in as such
		write_bone_motion(out, bm, num_frames, true);
	}

	int needed = int(out.bytes.size());
	if (buffer == 0 || size < needed || needed == 0)
		return needed;

	std::memcpy(buffer, &out.bytes[0], size_t(needed));
	return needed;
}

// ---- motion marks ------------------------------------------------------
// An SDK file keeps them (version 7 of a motion is version 6 plus these), so
// they survive a trip through .skls as long as the editor asks for them.

_declspec(dllexport) int SklGetMarkCount(int index)
{
	if (index < 0 || index >= int(g_motions.size()))
		return SKL_UNKNOWN_MOTION;
	return int(g_motions[size_t(index)]->marks().size());
}

_declspec(dllexport) int SklGetMarkName(int index, int mark, char* buffer, int size)
{
	if (index < 0 || index >= int(g_motions.size()))
		return copy_string(std::string(), buffer, size);
	const xr_motion_marks_vec& marks = g_motions[size_t(index)]->marks();
	if (mark < 0 || mark >= int(marks.size()) || marks[size_t(mark)] == 0)
		return copy_string(std::string(), buffer, size);
	return copy_string(marks[size_t(mark)]->name(), buffer, size);
}

// The intervals of one mark, as pairs of floats: t0, t1, t0, t1... Returns the
// number of pairs, or the number needed when the buffer is too small.
_declspec(dllexport) int SklGetMarkIntervals(int index, int mark, float* buffer, int size)
{
	if (index < 0 || index >= int(g_motions.size()))
		return SKL_UNKNOWN_MOTION;
	const xr_motion_marks_vec& marks = g_motions[size_t(index)]->marks();
	if (mark < 0 || mark >= int(marks.size()) || marks[size_t(mark)] == 0)
		return SKL_BAD_ARGUMENT;

	const xr_motion_marks& mm = *marks[size_t(mark)];
	int needed = int(mm.size());
	if (buffer == 0 || size < needed)
		return needed;
	for (int i = 0; i != needed; ++i) {
		buffer[i*2] = mm[size_t(i)].t0;
		buffer[i*2 + 1] = mm[size_t(i)].t1;
	}
	return needed;
}

_declspec(dllexport) void SklClose()
{
	clear_motions();
	g_skl_error.clear();
}

} // extern "C"
