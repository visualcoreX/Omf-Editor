using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace OMF_Editor
{
    // The last few OMF files, kept in the ini next to the editor and offered
    // straight from the File menu.
    public partial class OMF_Editor
    {
        const int RecentFilesCount = 5;

        private IniFile recentSettings;
        private ToolStripMenuItem recentFilesItem;
        private List<string> recentFiles = new List<string>();

        // Called from the form constructor.
        private void InitRecentFiles()
        {
            recentSettings = new IniFile(Path.Combine(
                Path.GetDirectoryName(Application.ExecutablePath), "OMF_Editor.ini"));

            recentFilesItem = new ToolStripMenuItem("Recent files");

            // right below Load, where it is looked for
            int at = fileToolStripMenuItem.DropDownItems.IndexOf(loasToolStripMenuItem);
            fileToolStripMenuItem.DropDownItems.Insert(at + 1, recentFilesItem);

            LoadRecentFiles();
            UpdateRecentFilesMenu();
        }

        private void LoadRecentFiles()
        {
            recentFiles.Clear();
            for (int i = 0; i != RecentFilesCount; ++i)
            {
                string path = recentSettings.Read("Recent" + i);
                if (!string.IsNullOrEmpty(path) && !recentFiles.Contains(path))
                    recentFiles.Add(path);
            }
        }

        private void SaveRecentFiles()
        {
            for (int i = 0; i != RecentFilesCount; ++i)
                recentSettings.Write("Recent" + i, i < recentFiles.Count ? recentFiles[i] : "");
        }

        // Newly opened files go first, and the list never grows past its size.
        private void AddRecentFile(string path)
        {
            if (string.IsNullOrEmpty(path) || recentFilesItem == null)
                return;

            recentFiles.RemoveAll(delegate(string known)
            {
                return string.Equals(known, path, StringComparison.OrdinalIgnoreCase);
            });
            recentFiles.Insert(0, path);
            while (recentFiles.Count > RecentFilesCount)
                recentFiles.RemoveAt(recentFiles.Count - 1);

            SaveRecentFiles();
            UpdateRecentFilesMenu();
        }

        private void UpdateRecentFilesMenu()
        {
            recentFilesItem.DropDownItems.Clear();

            if (recentFiles.Count == 0)
            {
                ToolStripMenuItem empty = new ToolStripMenuItem("(empty)");
                empty.Enabled = false;
                recentFilesItem.DropDownItems.Add(empty);
                return;
            }

            for (int i = 0; i != recentFiles.Count; ++i)
            {
                string path = recentFiles[i];
                // the file name reads well enough, the full path goes in the tip
                ToolStripMenuItem item = new ToolStripMenuItem(
                    string.Format("&{0}  {1}", i + 1, Path.GetFileName(path)));
                item.ToolTipText = path;
                item.Tag = path;
                item.Click += RecentFileClick;
                recentFilesItem.DropDownItems.Add(item);
            }

            recentFilesItem.DropDownItems.Add(new ToolStripSeparator());

            ToolStripMenuItem clear = new ToolStripMenuItem("Clear list");
            clear.Click += RecentFilesClearClick;
            recentFilesItem.DropDownItems.Add(clear);
        }

        private void RecentFileClick(object sender, EventArgs e)
        {
            string path = (string)((ToolStripMenuItem)sender).Tag;

            if (!File.Exists(path))
            {
                if (MessageBox.Show("File not found:\n" + path + "\n\nRemove it from the list?",
                        "Recent files", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    recentFiles.Remove(path);
                    SaveRecentFiles();
                    UpdateRecentFilesMenu();
                }
                return;
            }

            try
            {
                OpenFile(path);
            }
            catch (Exception exp)
            {
                MessageBox.Show(exp.ToString());
            }
        }

        private void RecentFilesClearClick(object sender, EventArgs e)
        {
            recentFiles.Clear();
            SaveRecentFiles();
            UpdateRecentFilesMenu();
        }
    }
}
