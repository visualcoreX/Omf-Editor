// Viewport support for the OMF editor: keeps a skinned OGF model and a set of
// OMF motions loaded, and bakes any of them into a .glb the embedded f3d viewer
// can play back. All entry points are meant to be called from a single thread.

#include <cctype>
#include <cstring>
#include <string>
#include <vector>

#include "gltf_export.h"
#include "xr_file_system.h"
#include "xr_ogf.h"
#include "xr_ogf_v4.h"
#include "xr_skl_motion.h"

using namespace xray_re;

namespace {

enum {
	VP_OK			= 0,
	VP_BAD_ARGUMENT		= -1,
	VP_NO_MODEL		= -2,
	VP_NO_MOTIONS		= -3,
	VP_LOAD_FAILED		= -4,
	VP_UNKNOWN_MOTION	= -5,
	VP_EXPORT_FAILED	= -6,
};

std::string g_error;
xr_ogf* g_model = 0;
xr_ogf_v4* g_motions = 0;

// Texture names the loaded model asks for, in the order its visuals appear, and
// the png the editor converted each of them into.
std::vector<std::string> g_texture_names;
ogf_texture_map g_textures;

void collect_texture_names(const xr_ogf& ogf, std::vector<std::string>& out)
{
	const std::vector<xr_ogf*>& children = ogf.children();
	if (children.empty()) {
		const std::string& name = ogf.texture();
		if (!name.empty() && ogf.vb().size() && ogf.ib().size()) {
			for (size_t i = 0; i != out.size(); ++i) {
				if (out[i] == name)
					return;
			}
			out.push_back(name);
		}
		return;
	}
	for (std::vector<xr_ogf*>::const_iterator it = children.begin(), end = children.end();
			it != end; ++it) {
		collect_texture_names(**it, out);
	}
}

int copy_out(const std::string& value, char* buffer, int size)
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

void ensure_file_system()
{
	static bool initialized = false;
	if (!initialized) {
		xr_file_system::instance().initialize(0, 0);
		initialized = true;
	}
}

bool iequals(const std::string& a, const std::string& b)
{
	if (a.size() != b.size())
		return false;
	for (size_t i = 0; i != a.size(); ++i) {
		unsigned char l = static_cast<unsigned char>(a[i]);
		unsigned char r = static_cast<unsigned char>(b[i]);
		if (std::tolower(l) != std::tolower(r))
			return false;
	}
	return true;
}

xr_skl_motion* find_motion(const char* name)
{
	std::string wanted(name);
	xr_skl_motion_vec& motions = g_motions->motions();
	for (xr_skl_motion_vec_it it = motions.begin(), end = motions.end(); it != end; ++it) {
		if ((*it)->name() == wanted)
			return *it;
	}
	for (xr_skl_motion_vec_it it = motions.begin(), end = motions.end(); it != end; ++it) {
		if (iequals((*it)->name(), wanted))
			return *it;
	}
	return 0;
}

} // anonymous namespace

extern "C" {

// Drops everything held by the viewport backend.
_declspec(dllexport) void ViewportReset()
{
	delete g_model;
	g_model = 0;
	delete g_motions;
	g_motions = 0;
	g_texture_names.clear();
	g_textures.clear();
	g_error.clear();
}

// Copies the last error message out, returns its length.
_declspec(dllexport) int ViewportGetLastError(char* buffer, int size)
{
	return copy_out(g_error, buffer, size);
}

// The textures the loaded model refers to, so the editor can convert them.
_declspec(dllexport) int ViewportGetTextureCount()
{
	return int(g_texture_names.size());
}

_declspec(dllexport) int ViewportGetTextureName(int index, char* buffer, int size)
{
	if (index < 0 || index >= int(g_texture_names.size()))
		return copy_out(std::string(), buffer, size);
	return copy_out(g_texture_names[size_t(index)], buffer, size);
}

// Hands back the png that texture was converted into. An empty path drops it,
// leaving that part of the model untextured.
_declspec(dllexport) int ViewportSetTextureImage(int index, const char* png_path)
{
	if (index < 0 || index >= int(g_texture_names.size()))
		return VP_BAD_ARGUMENT;
	const std::string& name = g_texture_names[size_t(index)];
	if (png_path == 0 || png_path[0] == '\0')
		g_textures.erase(name);
	else
		g_textures[name] = png_path;
	return VP_OK;
}

// Loads the skinned model whose mesh and skeleton the motions are played on.
_declspec(dllexport) int ViewportLoadModel(const char* ogf_path)
{
	g_error.clear();
	if (ogf_path == 0 || ogf_path[0] == '\0') {
		g_error = "no model path given";
		return VP_BAD_ARGUMENT;
	}
	ensure_file_system();

	xr_ogf* model = 0;
	try {
		model = xr_ogf::load_ogf(std::string(ogf_path));
	} catch (...) {
		model = 0;
	}
	if (model == 0) {
		g_error = "can't load ";
		g_error += ogf_path;
		return VP_LOAD_FAILED;
	}
	if (model->bones().empty()) {
		delete model;
		g_error = "model has no skeleton";
		return VP_LOAD_FAILED;
	}

	delete g_model;
	g_model = model;

	// the textures belong to the model, so a new one starts with none resolved
	g_texture_names.clear();
	g_textures.clear();
	collect_texture_names(*g_model, g_texture_names);
	return VP_OK;
}

// Loads (or reloads) the motion library the editor is working on.
_declspec(dllexport) int ViewportLoadMotions(const char* omf_path)
{
	g_error.clear();
	if (omf_path == 0 || omf_path[0] == '\0') {
		g_error = "no OMF path given";
		return VP_BAD_ARGUMENT;
	}
	ensure_file_system();

	xr_ogf_v4* motions = new xr_ogf_v4;
	bool loaded = false;
	try {
		loaded = motions->load_omf(omf_path);
	} catch (...) {
		loaded = false;
	}
	if (!loaded || motions->motions().empty()) {
		delete motions;
		g_error = "can't load ";
		g_error += omf_path;
		return VP_LOAD_FAILED;
	}

	delete g_motions;
	g_motions = motions;
	return VP_OK;
}

// Bakes the model, and the named motion when given, into a binary glTF file.
// Pass an empty motion name to get the bind pose only. A negative `frame` bakes
// the motion as a playable animation, a non-negative one freezes that frame -
// fractional frames land between two keys.
_declspec(dllexport) int ViewportBuildGLBTime(const char* motion_name, const char* out_path,
		double frame)
{
	g_error.clear();
	if (out_path == 0 || out_path[0] == '\0') {
		g_error = "no output path given";
		return VP_BAD_ARGUMENT;
	}
	if (g_model == 0) {
		g_error = "no model loaded";
		return VP_NO_MODEL;
	}

	xr_skl_motion* motion = 0;
	if (motion_name != 0 && motion_name[0] != '\0') {
		if (g_motions == 0) {
			g_error = "no motions loaded";
			return VP_NO_MOTIONS;
		}
		motion = find_motion(motion_name);
		if (motion == 0) {
			g_error = "unknown motion ";
			g_error += motion_name;
			return VP_UNKNOWN_MOTION;
		}
	}

	bool ok = false;
	try {
		ok = ogf_export_glb(*g_model, motion, float(frame), g_textures, out_path, g_error);
	} catch (...) {
		ok = false;
		if (g_error.empty())
			g_error = "unhandled error while building the preview";
	}
	return ok ? VP_OK : VP_EXPORT_FAILED;
}

// Whole frames only, kept for callers that predate the scrubber.
_declspec(dllexport) int ViewportBuildGLB(const char* motion_name, const char* out_path,
		int frame)
{
	return ViewportBuildGLBTime(motion_name, out_path, double(frame));
}

// Bone counts, so the editor can warn about a model/motion skeleton mismatch.
_declspec(dllexport) int ViewportGetModelBoneCount()
{
	return g_model ? int(g_model->bones().size()) : 0;
}

_declspec(dllexport) int ViewportGetMotionBoneCount()
{
	return g_motions ? int(g_motions->bones().size()) : 0;
}

} // extern "C"
