using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace OMF_Editor
{
    // Pulling motions out of SDK files. A .skl holds one, a .skls holds many,
    // and both keep their keys as envelopes - converter.dll samples them frame
    // by frame and packs them the way an OMF stores motions, for the bones of
    // the file currently open.
    public partial class OMF_Editor
    {
        [DllImport("converter.dll", CharSet = CharSet.Ansi)]
        private static extern int SklOpen(string path);

        [DllImport("converter.dll", CharSet = CharSet.Ansi)]
        private static extern int SklGetLastError(StringBuilder buffer, int size);

        [DllImport("converter.dll", CharSet = CharSet.Ansi)]
        private static extern int SklGetName(int index, StringBuilder buffer, int size);

        [DllImport("converter.dll")]
        private static extern int SklGetParams(int index, out float speed, out float accrue,
            out float falloff, out float power, out int flags, out int bone_or_part, out int frames);

        [DllImport("converter.dll", CharSet = CharSet.Ansi)]
        private static extern int SklGetData(int index, string bone_names, byte[] buffer, int size);

        [DllImport("converter.dll")]
        private static extern void SklClose();

        [DllImport("converter.dll")]
        private static extern int SklGetMarkCount(int index);

        [DllImport("converter.dll", CharSet = CharSet.Ansi)]
        private static extern int SklGetMarkName(int index, int mark, StringBuilder buffer, int size);

        [DllImport("converter.dll")]
        private static extern int SklGetMarkIntervals(int index, int mark, float[] buffer, int size);

        [DllImport("converter.dll")]
        private static extern int SklGetBoneCount();

        [DllImport("converter.dll", CharSet = CharSet.Ansi)]
        private static extern int SklGetBoneName(int index, StringBuilder buffer, int size);

        private ToolStripMenuItem sklImportItem;

        // Called from the form constructor.
        private void InitSklImport()
        {
            sklImportItem = new ToolStripMenuItem("Load/add from skl/skls...");
            sklImportItem.Click += SklImportClick;

            // in File, right after the recent files: it is a way of getting
            // motions in, same as opening a file
            int at = fileToolStripMenuItem.DropDownItems.IndexOf(recentFilesItem);
            fileToolStripMenuItem.DropDownItems.Insert(at + 1, sklImportItem);
        }

        // The bones of the open OMF in their own order, which is the order the
        // key streams of a motion follow.
        private List<string> OmfBoneNames()
        {
            SortedDictionary<uint, string> byId = new SortedDictionary<uint, string>();
            foreach (BoneParts part in Main_OMF.bone_cont.parts)
            {
                foreach (BoneVector bone in part.bones)
                {
                    if (!byId.ContainsKey(bone.ID))
                        byId.Add(bone.ID, bone.Name);
                }
            }

            List<string> names = new List<string>();
            foreach (KeyValuePair<uint, string> bone in byId)
                names.Add(bone.Value);
            return names;
        }

        // The marks of one motion of the loaded SDK file. They live in it the
        // same as in an OMF, so a motion that goes out to .skls and comes back
        // keeps them. Returns null for a motion that has none, and for a
        // converter.dll too old to be asked.
        private List<MotionMark> SklMotionMarks(int index)
        {
            int count;
            try
            {
                count = SklGetMarkCount(index);
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
            if (count <= 0)
                return null;

            List<MotionMark> marks = new List<MotionMark>();
            StringBuilder buffer = new StringBuilder(512);
            for (int m = 0; m != count; ++m)
            {
                buffer.Length = 0;
                SklGetMarkName(index, m, buffer, buffer.Capacity);

                MotionMark mark = new MotionMark();
                mark.Name = buffer.ToString();

                int intervals = SklGetMarkIntervals(index, m, null, 0);
                if (intervals > 0)
                {
                    float[] values = new float[intervals*2];
                    if (SklGetMarkIntervals(index, m, values, intervals) == intervals)
                    {
                        for (int k = 0; k != intervals; ++k)
                        {
                            MotionMarkParams param = new MotionMarkParams();
                            param.t0 = values[k*2];
                            param.t1 = values[k*2 + 1];
                            mark.m_params.Add(param);
                        }
                    }
                }
                mark.Count = mark.m_params.Count;
                marks.Add(mark);
            }
            return marks;
        }

        private static string SklLastError()
        {
            StringBuilder buffer = new StringBuilder(1024);
            SklGetLastError(buffer, buffer.Capacity);
            string text = buffer.ToString();
            return text.Length > 0 ? text : "unknown error";
        }

        // ---- bone parts alongside an SDK file --------------------------------
        // A .skl/.skls holds motions and nothing else: the bone parts an OMF is
        // split into have no place in it. So they are written beside the export
        // and picked up again on the way back, which keeps a round trip whole.
        // A file without this companion loads as it always did, one part.

        public static string BonePartsSidecar(string sklPath)
        {
            return sklPath + ".parts";
        }

        public static void WriteBonePartsSidecar(string sklPath, BoneContainer bones)
        {
            try
            {
                if (bones == null || bones.parts.Count < 2)
                    return;		// one part is what an import makes anyway

                StringBuilder text = new StringBuilder();
                text.AppendLine("# bone parts of the OMF this file came from, for OMF Editor");
                foreach (BoneParts part in bones.parts)
                {
                    List<string> ids = new List<string>();
                    foreach (BoneVector bone in part.bones)
                        ids.Add(bone.ID.ToString());
                    text.AppendLine(part.Name + "=" + string.Join(",", ids.ToArray()));
                }
                File.WriteAllText(BonePartsSidecar(sklPath), text.ToString());
            }
            catch (Exception exp)
            {
                Debug.WriteLine("bone parts sidecar: " + exp.Message);
            }
        }

        // Puts the parts back into a skeleton just built from an SDK file. Left
        // alone when there is no companion file, or when it does not describe
        // this skeleton.
        private static void ApplyBonePartsSidecar(string sklPath, BoneContainer bones, IList<string> boneNames)
        {
            string sidecar = BonePartsSidecar(sklPath);
            if (!File.Exists(sidecar))
                return;

            try
            {
                List<BoneParts> parts = new List<BoneParts>();
                foreach (string line in File.ReadAllLines(sidecar))
                {
                    string text = line.Trim();
                    if (text.Length == 0 || text[0] == '#')
                        continue;
                    int split = text.IndexOf('=');
                    if (split <= 0)
                        continue;

                    BoneParts part = new BoneParts();
                    part.Name = text.Substring(0, split);
                    foreach (string id in text.Substring(split + 1).Split(','))
                    {
                        uint value;
                        if (!uint.TryParse(id.Trim(), out value) || value >= boneNames.Count)
                            return;		// not this skeleton - leave the one part alone
                        BoneVector bone = new BoneVector();
                        bone.Name = boneNames[(int)value];
                        bone.ID = value;
                        part.bones.Add(bone);
                    }
                    part.Count = (short)part.bones.Count;
                    parts.Add(part);
                }

                if (parts.Count < 2)
                    return;

                bones.parts.Clear();
                bones.parts.AddRange(parts);
                bones.Count = (short)parts.Count;
            }
            catch (Exception exp)
            {
                Debug.WriteLine("bone parts sidecar: " + exp.Message);
            }
        }

        // The bones the loaded SDK file was made for. Used to start an OMF from
        // nothing when the editor has none open.
        private List<string> SklBoneNames()
        {
            List<string> names = new List<string>();
            int count = SklGetBoneCount();
            StringBuilder buffer = new StringBuilder(512);
            for (int i = 0; i != count; ++i)
            {
                buffer.Length = 0;
                SklGetBoneName(i, buffer, buffer.Capacity);
                names.Add(buffer.ToString());
            }
            return names;
        }

        // Builds an OMF around the motions just read, with their own skeleton.
        private bool StartOmfFromSkl(string sklPath)
        {
            List<string> bones = SklBoneNames();
            if (bones.Count == 0)
            {
                MessageBox.Show("The file carries no skeleton to build an OMF around.",
                    "Load/add from skl/skls", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            Main_OMF = new AnimationsContainer();
            Main_OMF.bone_cont = new BoneContainer(bones, 4);
            ApplyBonePartsSidecar(sklPath, Main_OMF.bone_cont, bones);
            Main_OMF.FileName = "";

            bs.DataSource = Main_OMF.AnimsParams;
            lbxMotions.DataSource = bs;
            lbxMotions.DisplayMember = "Name";

            LabelStatusFile.Text = "untitled";
            UpdateBonePartsWarning();		// a skeleton from an SDK file always has names
            saveAsToolStripMenuItem.Enabled = true;
            saveToolStripMenuItem.Enabled = true;
            toolsToolStripMenuItem.Enabled = true;
            showBonePartsToolStripMenuItem.Enabled = true;
            return true;
        }

        private void SklImportClick(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Filter = "SDK motion (*.skl, *.skls)|*.skl;*.skls|Skls file|*.skls|Skl file|*.skl";
                dialog.Title = "Load motions from an SDK file";
                dialog.Multiselect = true;
                if (dialog.ShowDialog() != DialogResult.OK)
                    return;

                ImportSklFiles(dialog.FileNames);
            }
        }

        // The files of one import, whether they were picked in the dialog or
        // dropped on the window.
        private void ImportSklFiles(IList<string> paths)
        {
            int added = 0, replaced = 0, skipped = 0;
            foreach (string path in paths)
                ImportSklFile(path, ref added, ref replaced, ref skipped);

            if (Main_OMF == null || (added + replaced == 0 && skipped == 0))
                return;

            Main_OMF.RecalcAllAnimIndex();
            Main_OMF.RecalcAnimNum();
            UpdateList();
            RequestViewportUpdate(true);

            string report = added + " motion(s) added";
            if (replaced != 0)
                report += ", " + replaced + " replaced";
            if (skipped != 0)
                report += ", " + skipped + " skipped";
            MessageBox.Show(report, "Load/add from skl/skls", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void ImportSklFile(string path, ref int added, ref int replaced, ref int skipped)
        {
            int count;
            try
            {
                count = SklOpen(path);
            }
            catch (DllNotFoundException)
            {
                MessageBox.Show("Can't find converter.dll", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            catch (EntryPointNotFoundException)
            {
                MessageBox.Show("converter.dll is too old for this: it has no skl reader.",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (count <= 0)
            {
                MessageBox.Show("Can't read " + Path.GetFileName(path) + ":\n" + SklLastError(),
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SklClose();
                return;
            }

            try
            {
                // with nothing open, the file itself supplies the skeleton
                if (Main_OMF == null && !StartOmfFromSkl(path))
                    return;

                // Bones of an OMF that keeps its parts as bare ids come out
                // nameless. They are passed on as they are - the reader takes an
                // empty name to mean the bone in that place of the motion.
                string bones = string.Join("\n", OmfBoneNames().ToArray());
                bool? overwriteAll = chbxAskForOverwrite.Checked ? (bool?)null : true;

                StringBuilder buffer = new StringBuilder(512);
                for (int i = 0; i != count; ++i)
                {
                    buffer.Length = 0;
                    SklGetName(i, buffer, buffer.Capacity);
                    string name = buffer.ToString();

                    // a file of one motion is named for it - exporters tend to
                    // leave the name inside as "unnamed" or the like
                    if (count == 1)
                        name = Path.GetFileNameWithoutExtension(path);

                    if (name.Length == 0)
                        continue;

                    int existing = Main_OMF.AnimsParams.FindIndex(delegate(AnimationParams p)
                    {
                        return p.Name == name;
                    });

                    if (existing >= 0)
                    {
                        if (overwriteAll == null)
                        {
                            DialogResult answer = MessageBox.Show(
                                "Motion \"" + name + "\" is already there. Overwrite it?\n\n" +
                                "No overwrites the rest as well when you answer for all.",
                                "Load/add from skl/skls", MessageBoxButtons.YesNoCancel,
                                MessageBoxIcon.Question);
                            if (answer == DialogResult.Cancel)
                                return;
                            overwriteAll = answer == DialogResult.Yes;
                        }
                        if (!overwriteAll.Value)
                        {
                            ++skipped;
                            continue;
                        }
                    }

                    float speed, accrue, falloff, power;
                    int flags, boneOrPart, frames;
                    if (SklGetParams(i, out speed, out accrue, out falloff, out power,
                            out flags, out boneOrPart, out frames) != 0)
                    {
                        ++skipped;
                        continue;
                    }

                    int size = SklGetData(i, bones, null, 0);
                    if (size <= 0)
                    {
                        ++skipped;
                        continue;
                    }

                    byte[] data = new byte[size];
                    if (SklGetData(i, bones, data, size) != size)
                    {
                        ++skipped;
                        continue;
                    }

                    AnimVector vector = new AnimVector();
                    vector.Name = name;
                    vector.data = data;
                    vector.RecalcSectionSize();

                    AnimationParams param = new AnimationParams();
                    param.Name = name;
                    param.Flags = flags;
                    param.BoneOrPart = (short)boneOrPart;
                    param.Speed = speed;
                    param.Power = power;
                    param.Accrue = accrue;
                    param.Falloff = falloff;
                    param.m_marks = SklMotionMarks(i);
                    param.MarksCount = param.m_marks != null ? param.m_marks.Count : 0;

                    if (existing >= 0)
                    {
                        Main_OMF.Anims[existing] = vector;
                        Main_OMF.AnimsParams[existing] = param;
                        ++replaced;
                    }
                    else
                    {
                        Main_OMF.AddAnim(vector);
                        Main_OMF.AddAnimParams(param);
                        ++added;
                    }
                }
            }
            finally
            {
                SklClose();
            }
        }
    }
}
