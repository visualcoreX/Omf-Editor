#ifndef __GNUC__
#pragma once
#endif
#ifndef __GLTF_EXPORT_H__
#define __GLTF_EXPORT_H__

#include <map>
#include <string>

namespace xray_re {
	class xr_ogf;
	class xr_skl_motion;
}

// Texture name of a visual, as the OGF spells it, to the png file the preview
// should use for it. Names missing from the map are exported untextured.
typedef std::map<std::string, std::string> ogf_texture_map;

// Writes a binary glTF (.glb) preview of a skinned OGF model: geometry, skin and
// skeleton always, plus the given motion when it is not null. A negative `frame`
// bakes the whole motion as a glTF animation, otherwise the model is frozen in
// the pose of that frame - which is how the editor scrubs a motion. The frame is
// fractional: the motion envelopes are sampled at that point in time, so the
// scrubber can stop between two keys.
// Everything is converted from the X-Ray left handed basis to the right handed
// one glTF expects, so viewers with glTF skinning support (f3d) play it as is.
bool ogf_export_glb(const xray_re::xr_ogf& ogf, const xray_re::xr_skl_motion* motion,
		float frame, const ogf_texture_map& textures, const char* out_path,
		std::string& error);

#endif
