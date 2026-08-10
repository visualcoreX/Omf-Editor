using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace OMF_Editor
{
    // Right hand side 3D viewport. A skinned OGF model is loaded next to the OMF
    // being edited, the selected motion is baked into a glTF file by converter.dll
    // and an f3d process is embedded into the panel to play it back.
    public partial class OMF_Editor
    {
        const int ViewportDefaultWidth = 560;
        const int ViewportMinWidth = 260;
        // trackbar positions per motion frame - the pose is sampled from the
        // motion envelopes, so stopping between two keys is just as valid as on
        // one, and enough of them per frame make the scrubber feel continuous
        const int ViewportFrameSteps = 100;
        const float MotionFps = 30.0f;

        [DllImport("user32.dll")]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint code, uint type);

        const int GWL_STYLE = -16;
        const int WS_CAPTION = 0x00C00000;
        const int WS_THICKFRAME = 0x00040000;
        const int WS_SYSMENU = 0x00080000;
        const int WS_MINIMIZEBOX = 0x00020000;
        const int WS_MAXIMIZEBOX = 0x00010000;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_FRAMECHANGED = 0x0020;
        const uint SWP_SHOWWINDOW = 0x0040;
        const uint WM_KEYDOWN = 0x0100;
        const uint WM_KEYUP = 0x0101;
        const int VK_SPACE = 0x20;

        [DllImport("converter.dll", CharSet = CharSet.Ansi)]
        private static extern int ViewportLoadModel(string ogf_path);

        [DllImport("converter.dll", CharSet = CharSet.Ansi)]
        private static extern int ViewportLoadMotions(string omf_path);

        [DllImport("converter.dll", CharSet = CharSet.Ansi)]
        private static extern int ViewportBuildGLBTime(string motion_name, string out_path, double frame);

        [DllImport("converter.dll", CharSet = CharSet.Ansi)]
        private static extern int ViewportGetLastError(StringBuilder buffer, int size);

        [DllImport("converter.dll")]
        private static extern int ViewportGetModelBoneCount();

        [DllImport("converter.dll")]
        private static extern int ViewportGetMotionBoneCount();

        [DllImport("converter.dll")]
        private static extern void ViewportReset();

        [DllImport("converter.dll")]
        private static extern int ViewportGetTextureCount();

        [DllImport("converter.dll", CharSet = CharSet.Ansi)]
        private static extern int ViewportGetTextureName(int index, StringBuilder buffer, int size);

        [DllImport("converter.dll", CharSet = CharSet.Ansi)]
        private static extern int ViewportSetTextureImage(int index, string png_path);

        private IniFile viewportSettings;

        private ToolStripMenuItem viewportShowItem;
        private ToolStripMenuItem viewportAutoPlayItem;
        private ToolStripButton viewportTearButton;
        private ToolStripMenuItem viewportTearOnItem;
        private bool viewportTornOff;
        private int viewportRestoreWidth;	// panel width to come back to
        private bool viewportStoppingViewer;	// our own kill, not the user closing it

        private Panel viewportPanel;
        private Panel viewportHost;
        private Button viewportAppendButton;
        private ToolStrip viewportToolStrip;
        private StatusStrip viewportStatusStrip;
        private ToolStripStatusLabel viewportStatusLabel;
        private System.Windows.Forms.Timer viewportUpdateTimer;

        private string viewportTexturesPath = "";
        private ToolStripMenuItem viewportTexturesItem;

        private Process viewerProcess;
        // reparenting hides the window from Process.MainWindowHandle, which
        // starts returning zero, so the handle is kept from before the embed
        private IntPtr viewerWindow;
        private string viewportModelPath = "";
        private bool viewportEnabled;
        private bool viewportBusy;
        private bool viewportRestartPending;
        private bool viewportStarted;
        private bool viewportLayoutSuspended;
        private int viewportBaseClientWidth;
        private int viewportWidth = ViewportDefaultWidth;
        private string viewportHelpText;

        private string ViewportAppFolder
        {
            get { return Path.GetDirectoryName(Application.ExecutablePath); }
        }

        // Next to the editor normally. Running straight out of an intermediate
        // build folder finds the copy in the source tree instead.
        // The f3d distribution, deployed next to the editor or picked out of the
        // source tree when running from a build folder. The single exe variants
        // come last: those are the old build, which has neither --watch nor a
        // working glTF skin, and is only good enough to show something.
        private string ViewportExePath
        {
            get
            {
                string local = Path.Combine(ViewportAppFolder, @"f3d\bin\f3d.exe");
                if (File.Exists(local))
                    return local;

                for (DirectoryInfo dir = new DirectoryInfo(ViewportAppFolder); dir != null; dir = dir.Parent)
                {
                    string sdk = Path.Combine(dir.FullName, @"SDK\f3d\bin\f3d.exe");
                    if (File.Exists(sdk))
                        return sdk;
                }

                string legacy = Path.Combine(ViewportAppFolder, "f3d.exe");
                if (File.Exists(legacy))
                    return legacy;

                for (DirectoryInfo dir = new DirectoryInfo(ViewportAppFolder); dir != null; dir = dir.Parent)
                {
                    string sdk = Path.Combine(dir.FullName, @"SDK\binaries\f3d.exe");
                    if (File.Exists(sdk))
                        return sdk;
                }
                return local;
            }
        }

        // Beside the editor rather than in the system temp: everything in it is
        // written for the viewport and wiped when the editor closes.
        private string ViewportTempFolder
        {
            get
            {
                string folder = Path.Combine(ViewportAppFolder, "viewport_cache");
                if (!Directory.Exists(folder))
                    Directory.CreateDirectory(folder);
                return folder;
            }
        }

        // One file for the whole session: f3d runs with --watch and reloads it
        // by itself whenever we write a new pose into it, keeping its camera.
        private string ViewportGlbPath
        {
            get { return Path.Combine(ViewportTempFolder, "preview.glb"); }
        }

        private string ViewportOmfPath
        {
            get { return Path.Combine(ViewportTempFolder, "preview.omf"); }
        }

        // Called from the form constructor.
        private void InitViewport()
        {
            viewportSettings = new IniFile(Path.Combine(ViewportAppFolder, "OMF_Editor.ini"));

            CreateViewportControls();

            this.Shown += ViewportOnFormShown;
            this.FormClosing += ViewportOnFormClosing;
            this.Resize += ViewportOnFormResize;

            // space reaches the viewer from anywhere in the editor
            this.KeyPreview = true;
            this.KeyDown += ViewportOnKeyDown;
        }

        private void CreateViewportControls()
        {
            viewportPanel = new Panel();
            viewportPanel.Name = "viewportPanel";
            viewportPanel.BorderStyle = BorderStyle.FixedSingle;
            viewportPanel.Visible = false;	// positioned by LayoutViewport

            viewportToolStrip = new ToolStrip();
            viewportToolStrip.Dock = DockStyle.Top;
            viewportToolStrip.GripStyle = ToolStripGripStyle.Hidden;
            viewportToolStrip.RenderMode = ToolStripRenderMode.System;

            ToolStripButton loadButton = new ToolStripButton("Model...");
            loadButton.ToolTipText = "Load a skinned .ogf model to play the motions on";
            loadButton.Click += ViewportLoadModelClick;
            viewportToolStrip.Items.Add(loadButton);

            ToolStripButton reloadButton = new ToolStripButton("Reload");
            reloadButton.ToolTipText = "Rebuild the preview from the current motion";
            reloadButton.Click += ViewportReloadClick;
            viewportToolStrip.Items.Add(reloadButton);

            viewportTearButton = new ToolStripButton("Tear off");
            viewportTearButton.ToolTipText = "Show the viewer in a window of its own";
            viewportTearButton.Click += ViewportTearClick;
            viewportToolStrip.Items.Add(viewportTearButton);

            // scrubbing lives in the viewer itself: it draws a bar of its own
            // over the animation, and dragging that moves the model right away,
            // which no rebuild from here could match

            // a status line of its own, the tool strip would push it into the
            // overflow menu as soon as the viewport gets narrow
            viewportStatusStrip = new StatusStrip();
            viewportStatusStrip.Dock = DockStyle.Bottom;
            viewportStatusStrip.SizingGrip = false;
            viewportStatusLabel = new ToolStripStatusLabel("No model");
            viewportStatusLabel.Spring = true;
            viewportStatusLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            viewportStatusStrip.Items.Add(viewportStatusLabel);

            viewportHost = new Panel();
            viewportHost.Dock = DockStyle.Fill;
            viewportHost.BackColor = System.Drawing.Color.FromArgb(51, 51, 51);
            viewportHost.Resize += ViewportOnHostResize;

            // nothing to show until a model is picked, so the empty viewport is
            // the invitation to pick one
            viewportAppendButton = new Button();
            viewportAppendButton.Dock = DockStyle.Fill;
            viewportAppendButton.Text = "Append OGF";
            // the ordinary themed button of the Append OMF tab: light face, dark
            // text, rather than something that melts into the dark viewport
            viewportAppendButton.UseVisualStyleBackColor = true;
            viewportAppendButton.Font = new System.Drawing.Font(this.Font.FontFamily, 12F);
            viewportAppendButton.Click += ViewportLoadModelClick;
            viewportHost.Controls.Add(viewportAppendButton);

            viewportPanel.Controls.Add(viewportHost);
            viewportPanel.Controls.Add(viewportStatusStrip);
            viewportPanel.Controls.Add(viewportToolStrip);
            this.Controls.Add(viewportPanel);
            viewportPanel.BringToFront();

            viewportUpdateTimer = new System.Windows.Forms.Timer();
            viewportUpdateTimer.Interval = 250;
            viewportUpdateTimer.Tick += ViewportUpdateTimerTick;

            ToolStripMenuItem viewportMenu = new ToolStripMenuItem("Viewport");

            viewportShowItem = new ToolStripMenuItem("Show viewport");
            viewportShowItem.CheckOnClick = true;
            viewportShowItem.Click += ViewportShowClick;
            viewportMenu.DropDownItems.Add(viewportShowItem);

            ToolStripMenuItem loadModelItem = new ToolStripMenuItem("Load model...");
            loadModelItem.Click += ViewportLoadModelClick;
            viewportMenu.DropDownItems.Add(loadModelItem);

            viewportTearOnItem = new ToolStripMenuItem("Dock viewport");
            viewportTearOnItem.ToolTipText = "Put the viewer back into the editor";
            viewportTearOnItem.Visible = false;
            viewportTearOnItem.Click += ViewportTearClick;
            viewportMenu.DropDownItems.Add(viewportTearOnItem);

            viewportTexturesItem = new ToolStripMenuItem("Gamedata folder...");
            viewportTexturesItem.ToolTipText = "the gamedata of the mod the model belongs to, textures are taken from its textures folder\nset by itself for a model opened from inside a gamedata folder";
            viewportTexturesItem.Click += ViewportTexturesClick;
            viewportMenu.DropDownItems.Add(viewportTexturesItem);

            viewportAutoPlayItem = new ToolStripMenuItem("Autoplay on reload");
            viewportAutoPlayItem.CheckOnClick = true;
            viewportAutoPlayItem.Checked = true;
            viewportAutoPlayItem.Click += ViewportAutoPlayClick;
            viewportMenu.DropDownItems.Add(viewportAutoPlayItem);

            menuStrip1.Items.Add(viewportMenu);
        }

        private void ViewportOnFormShown(object sender, EventArgs e)
        {
            viewportBaseClientWidth = this.ClientSize.Width;

            viewportWidth = ViewportReadInt("ViewportWidth", ViewportDefaultWidth);
            if (viewportWidth < ViewportMinWidth)
                viewportWidth = ViewportDefaultWidth;

            viewportModelPath = viewportSettings.Read("ViewportModel");
            viewportTexturesPath = viewportSettings.Read("ViewportGamedata");
            if (string.IsNullOrEmpty(viewportTexturesPath))
                viewportTexturesPath = viewportSettings.Read("ViewportTextures");	// picked before gamedata was asked for
            viewportAutoPlayItem.Checked = ViewportReadInt("ViewportAutoPlay", 1) != 0;

            // on by default: the viewport is the point of the editor now, and it
            // still costs nothing until a model is loaded into it
            if (ViewportReadInt("ViewportEnabled", 1) != 0)
            {
                viewportShowItem.Checked = true;
                EnableViewport(true);
            }
        }

        private int ViewportReadInt(string key, int fallback)
        {
            int value;
            if (int.TryParse(viewportSettings.Read(key), out value))
                return value;
            return fallback;
        }

        private void ViewportShowClick(object sender, EventArgs e)
        {
            EnableViewport(viewportShowItem.Checked);
            viewportSettings.Write("ViewportEnabled", viewportEnabled ? "1" : "0");
        }

        private void EnableViewport(bool enable)
        {
            if (enable == viewportEnabled)
                return;

            viewportEnabled = enable;
            viewportShowItem.Checked = enable;

            if (enable)
            {
                if (!File.Exists(ViewportExePath))
                {
                    viewportEnabled = false;
                    viewportShowItem.Checked = false;
                    MessageBox.Show("Can't find f3d.exe next to the editor:\n" + ViewportExePath +
                        "\n\nThe viewport module is missing.",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // the intermediate resizes below must not be mistaken for the
                // user dragging the window edge
                viewportLayoutSuspended = true;
                this.FormBorderStyle = FormBorderStyle.Sizable;
                this.MaximizeBox = true;
                this.MinimumSize = new System.Drawing.Size(
                    this.Width - this.ClientSize.Width + viewportBaseClientWidth + ViewportMinWidth,
                    this.Height);
                this.ClientSize = new System.Drawing.Size(viewportBaseClientWidth + viewportWidth, this.ClientSize.Height);
                viewportPanel.Visible = true;
                viewportLayoutSuspended = false;
                LayoutViewport();

                if (!string.IsNullOrEmpty(viewportModelPath) && File.Exists(viewportModelPath))
                {
                    LoadViewportModel(viewportModelPath, false);
                }
                else
                {
                    viewportModelPath = "";		// a path that no longer exists is no model
                    viewportStatusLabel.Text = "No model";
                }
                UpdateViewportAppendButton();

                RequestViewportUpdate(true);
            }
            else
            {
                StopViewer();
                viewportLayoutSuspended = true;
                viewportPanel.Visible = false;
                this.MinimumSize = new System.Drawing.Size(0, 0);
                this.ClientSize = new System.Drawing.Size(viewportBaseClientWidth, this.ClientSize.Height);
                this.MaximizeBox = false;
                this.FormBorderStyle = FormBorderStyle.FixedDialog;
                viewportLayoutSuspended = false;
            }
        }

        private void LayoutViewport()
        {
            if (viewportPanel == null || !viewportEnabled || viewportLayoutSuspended)
                return;

            int top = menuStrip1 != null ? menuStrip1.Height : 0;
            int bottom = statusStrip1 != null ? statusStrip1.Height : 0;
            int left = viewportBaseClientWidth;
            int width = this.ClientSize.Width - left;
            int height = this.ClientSize.Height - top - bottom;
            if (width < 1) width = 1;
            if (height < 1) height = 1;

            viewportPanel.SetBounds(left, top, width, height);
            viewportWidth = width;
        }

        private void ViewportOnFormResize(object sender, EventArgs e)
        {
            LayoutViewport();
        }

        private void ViewportOnHostResize(object sender, EventArgs e)
        {
            ResizeEmbeddedViewer();
        }

        private void ResizeEmbeddedViewer()
        {
            if (viewportTornOff)
                return;	// a window of its own follows the user, not the panel
            if (viewerProcess == null || viewerProcess.HasExited || !viewportStarted || viewerWindow == IntPtr.Zero)
                return;
            try
            {
                SetWindowPos(viewerWindow, IntPtr.Zero, 0, 0,
                    viewportHost.Width, viewportHost.Height, SWP_NOACTIVATE | SWP_NOZORDER);
            }
            catch (Exception) { }
        }

        // Space plays and pauses the animation. The viewer only ever hears it
        // while its own window has the focus, so the editor passes it along -
        // unless something is being typed into, where a space is a space.
        private void ViewportOnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Space || e.Control || e.Alt)
                return;
            if (IsTypingControl(ActiveTextInput()))
                return;
            if (!ViewerRunning || viewerWindow == IntPtr.Zero)
                return;

            SendViewerKey(VK_SPACE);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        // The control the keyboard actually goes to, down through the containers.
        private Control ActiveTextInput()
        {
            Control control = this.ActiveControl;
            while (control is ContainerControl)
            {
                Control inner = ((ContainerControl)control).ActiveControl;
                if (inner == null)
                    break;
                control = inner;
            }
            return control;
        }

        private static bool IsTypingControl(Control control)
        {
            if (control is TextBoxBase)
                return true;
            ComboBox combo = control as ComboBox;
            return combo != null && combo.DropDownStyle != ComboBoxStyle.DropDownList;
        }

        private void SendViewerKey(int virtualKey)
        {
            try
            {
                uint scan = MapVirtualKey((uint)virtualKey, 0);
                IntPtr down = (IntPtr)(1 | (int)(scan << 16));
                IntPtr up = (IntPtr)(1 | (int)(scan << 16) | (1 << 30) | (1 << 31));
                PostMessage(viewerWindow, WM_KEYDOWN, (IntPtr)virtualKey, down);
                PostMessage(viewerWindow, WM_KEYUP, (IntPtr)virtualKey, up);
            }
            catch (Exception) { }
        }

        // ---- tearing the viewer off ------------------------------------------

        private void ViewportTearClick(object sender, EventArgs e)
        {
            ApplyViewportTearOff(!viewportTornOff);
        }

        // Moves the viewer window between the panel and a window of its own. The
        // frame has to be given back to it when it goes out and taken away again
        // when it comes back, and the style change only takes with FRAMECHANGED.
        private void ApplyViewportTearOff(bool tornOff)
        {
            viewportTornOff = tornOff;
            UpdateViewportTearButton();

            // the panel has nothing left to show while the viewer is out, so the
            // editor gives the width back instead of keeping an empty hole
            if (viewportEnabled)
            {
                viewportLayoutSuspended = true;
                if (tornOff)
                {
                    // kept aside: laying out a collapsed editor would otherwise
                    // record the viewport as one pixel wide
                    viewportRestoreWidth = viewportWidth;
                    viewportPanel.Visible = false;
                    this.MinimumSize = new System.Drawing.Size(0, 0);
                    this.ClientSize = new System.Drawing.Size(viewportBaseClientWidth, this.ClientSize.Height);
                }
                else
                {
                    if (viewportRestoreWidth >= ViewportMinWidth)
                        viewportWidth = viewportRestoreWidth;
                    this.MinimumSize = new System.Drawing.Size(
                        this.Width - this.ClientSize.Width + viewportBaseClientWidth + ViewportMinWidth,
                        this.Height);
                    this.ClientSize = new System.Drawing.Size(
                        viewportBaseClientWidth + viewportWidth, this.ClientSize.Height);
                    viewportPanel.Visible = true;
                }
                viewportLayoutSuspended = false;
                if (!tornOff)
                    LayoutViewport();
            }

            if (viewerWindow == IntPtr.Zero || !ViewerRunning)
                return;

            try
            {
                int style = GetWindowLong(viewerWindow, GWL_STYLE);
                if (tornOff)
                {
                    SetParent(viewerWindow, IntPtr.Zero);
                    style |= WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
                    SetWindowLong(viewerWindow, GWL_STYLE, style);

                    // beside the editor rather than on top of it, at a size worth
                    // tearing it off for
                    int width = Math.Max(viewportHost.Width, 900);
                    int height = Math.Max(viewportHost.Height, 700);
                    SetWindowPos(viewerWindow, IntPtr.Zero, this.Left + 40, this.Top + 40,
                        width, height, SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
                }
                else
                {
                    style = style & ~WS_CAPTION & ~WS_THICKFRAME;
                    SetWindowLong(viewerWindow, GWL_STYLE, style);
                    SetParent(viewerWindow, viewportHost.Handle);
                    SetWindowPos(viewerWindow, IntPtr.Zero, 0, 0,
                        viewportHost.Width, viewportHost.Height,
                        SWP_NOACTIVATE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
                }
            }
            catch (Exception exp)
            {
                Debug.WriteLine(exp.ToString());
            }
        }

        // The button lives on the viewport tool strip, which goes away with the
        // panel - so while the viewer is out, the menu carries the way back.
        private void UpdateViewportTearButton()
        {
            if (viewportTearButton != null)
                viewportTearButton.Text = "Tear off";
            if (viewportTearOnItem != null)
                viewportTearOnItem.Visible = viewportTornOff;
        }

        private void ViewportOnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (viewportEnabled)
            {
                viewportSettings.Write("ViewportWidth", viewportWidth.ToString());
                viewportSettings.Write("ViewportAutoPlay", viewportAutoPlayItem.Checked ? "1" : "0");
            }
            StopViewer();
            try { ViewportReset(); }
            catch (Exception) { }

            CleanViewportTempFolder();
        }

        // Nothing in there outlives the editor: the preview, the OMF dumped for
        // it and the converted textures are all rebuilt on demand next time.
        private void CleanViewportTempFolder()
        {
            try
            {
                foreach (string path in Directory.GetFiles(ViewportTempFolder))
                {
                    // a file the viewer has not let go of yet is left for the
                    // next run to clear
                    try { File.Delete(path); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (Exception exp)
            {
                Debug.WriteLine(exp.ToString());
            }
        }

        // ---- model -----------------------------------------------------------

        private void ViewportLoadModelClick(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Filter = "OGF model|*.ogf";
                dialog.Title = "Select a skinned model for the viewport";
                if (!string.IsNullOrEmpty(viewportModelPath))
                    dialog.FileName = viewportModelPath;
                if (dialog.ShowDialog() != DialogResult.OK)
                    return;

                if (!viewportEnabled)
                {
                    viewportShowItem.Checked = true;
                    EnableViewport(true);
                    if (!viewportEnabled)
                        return;
                }

                if (LoadViewportModel(dialog.FileName, true))
                    RequestViewportUpdate(true);
            }
        }

        private bool LoadViewportModel(string path, bool report)
        {
            int result;
            try
            {
                result = ViewportLoadModel(path);
            }
            catch (DllNotFoundException)
            {
                if (report)
                    MessageBox.Show("Can't find converter.dll", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (result != 0)
            {
                viewportModelPath = "";
                viewportStatusLabel.Text = "No model";
                UpdateViewportAppendButton();
                if (report)
                    MessageBox.Show("Can't load the model:\n" + ViewportLastError(), "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            viewportModelPath = path;
            viewportSettings.Write("ViewportModel", path);
            viewportStatusLabel.Text = Path.GetFileName(path) + " (" + ViewportGetModelBoneCount() + " bones)";
            AdoptGamedataOf(path);
            ResolveViewportTextures();
            UpdateViewportAppendButton();
            return true;
        }

        // The invitation covers the viewport for exactly as long as there is no
        // model to draw in it.
        private void UpdateViewportAppendButton()
        {
            if (viewportAppendButton != null)
                viewportAppendButton.Visible = string.IsNullOrEmpty(viewportModelPath);
        }

        // ---- textures --------------------------------------------------------

        private void ViewportTexturesClick(object sender, EventArgs e)
        {
            string start = Directory.Exists(viewportTexturesPath) ? viewportTexturesPath : "";
            string path = FolderPicker.Select(this, "Select the gamedata folder", start);
            if (string.IsNullOrEmpty(path))
                return;

            viewportTexturesPath = path;
            viewportSettings.Write("ViewportGamedata", viewportTexturesPath);

            if (!string.IsNullOrEmpty(viewportModelPath))
            {
                ResolveViewportTextures();
                RequestViewportUpdate(true);
            }
        }

        // The gamedata a model belongs to: the nearest folder named gamedata
        // above it that holds a textures folder of its own. A mod that ships
        // meshes only is skipped, so the gamedata picked by hand for it stays.
        private static string FindGamedataRoot(string modelPath)
        {
            try
            {
                DirectoryInfo dir = Directory.GetParent(Path.GetFullPath(modelPath));
                while (dir != null)
                {
                    if (string.Equals(dir.Name, "gamedata", StringComparison.OrdinalIgnoreCase)
                        && Directory.Exists(Path.Combine(dir.FullName, "textures")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch (Exception exp)
            {
                Debug.WriteLine(exp.ToString());
            }
            return null;
        }

        // A model opened from inside a gamedata tree already says where its
        // textures are, so the folder only has to be picked by hand for models
        // kept somewhere else.
        private void AdoptGamedataOf(string modelPath)
        {
            string gamedata = FindGamedataRoot(modelPath);
            if (gamedata == null || string.Equals(gamedata, viewportTexturesPath, StringComparison.OrdinalIgnoreCase))
                return;

            viewportTexturesPath = gamedata;
            viewportSettings.Write("ViewportGamedata", gamedata);
        }

        // Converts every texture the model asks for into a png the viewer can
        // read, and hands the results back to the exporter.
        private void ResolveViewportTextures()
        {
            int count;
            try
            {
                count = ViewportGetTextureCount();
            }
            catch (Exception)
            {
                return;
            }

            int converted = 0, missing = 0;
            StringBuilder buffer = new StringBuilder(512);
            for (int i = 0; i != count; ++i)
            {
                buffer.Length = 0;
                ViewportGetTextureName(i, buffer, buffer.Capacity);
                string name = buffer.ToString();
                if (name.Length == 0)
                    continue;

                string png = ViewportTexturePng(name);
                ViewportSetTextureImage(i, png ?? "");
                if (png != null)
                    ++converted;
                else
                    ++missing;
            }

            if (count != 0)
            {
                string model = Path.GetFileName(viewportModelPath);
                viewportStatusLabel.Text = string.Format("{0} - {1} of {2} textures", model,
                    converted, converted + missing);
            }
        }

        // Short stable stamp of a full path, to tell same named textures of
        // different mods apart in the cache folder.
        private static string PathKey(string path)
        {
            uint hash = 2166136261;
            foreach (char c in path.ToLowerInvariant())
                hash = (hash ^ c)*16777619;
            return hash.ToString("x8");
        }

        // The png of one texture, converted on demand and kept until the dds
        // changes. Returns null when there is nothing to convert.
        private string ViewportTexturePng(string name)
        {
            if (string.IsNullOrEmpty(viewportTexturesPath))
                return null;

            // gamedata normally, but a textures folder picked directly still works
            string root = Path.Combine(viewportTexturesPath, "textures");
            if (!Directory.Exists(root))
                root = viewportTexturesPath;

            string dds = Path.Combine(root, name.Replace('/', '\\') + ".dds");
            if (!File.Exists(dds))
                return null;

            string flat = name.Replace('\\', '_').Replace('/', '_');
            foreach (char bad in Path.GetInvalidFileNameChars())
                flat = flat.Replace(bad, '_');
            // the source folder belongs in the name: another mod can hold a
            // texture of the same name, and its dds is usually older than the
            // png converted here, so a date alone would keep the wrong one
            string png = Path.Combine(ViewportTempFolder,
                "tex_" + flat + "_" + PathKey(dds) + ".png");

            try
            {
                // still good within a session: the same model rebuilt over and
                // over does not pay for its textures again
                if (File.Exists(png) && File.GetLastWriteTimeUtc(png) >= File.GetLastWriteTimeUtc(dds))
                    return png;

                using (Bitmap bitmap = DdsImage.Load(dds))
                    bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                return png;
            }
            catch (Exception exp)
            {
                Debug.WriteLine("texture " + name + ": " + exp.Message);
                return null;
            }
        }

        private static string ViewportLastError()
        {
            StringBuilder buffer = new StringBuilder(1024);
            ViewportGetLastError(buffer, buffer.Capacity);
            string text = buffer.ToString();
            return text.Length > 0 ? text : "unknown error";
        }

        // ---- preview building ------------------------------------------------

        // Called whenever the selected motion (or the OMF itself) changes.
        private void RequestViewportUpdate(bool immediate = false, int delay = 250)
        {
            if (viewportUpdateTimer == null || !viewportEnabled)
                return;

            if (string.IsNullOrEmpty(viewportModelPath))
                return;

            viewportUpdateTimer.Stop();
            if (immediate)
            {
                ViewportUpdateTimerTick(null, EventArgs.Empty);
            }
            else
            {
                viewportUpdateTimer.Interval = delay;
                viewportUpdateTimer.Start();
            }
        }

        private void ViewportUpdateTimerTick(object sender, EventArgs e)
        {
            viewportUpdateTimer.Stop();

            if (!viewportEnabled || string.IsNullOrEmpty(viewportModelPath))
                return;

            if (viewportBusy)
            {
                viewportRestartPending = true;
                return;
            }

            string motion = "";
            string omfPath = null;

            if (Main_OMF != null && lbxMotions.SelectedItems.Count == 1)
            {
                AnimationParams current = GetCurrentMotion();
                if (current != null)
                {
                    motion = current.Name;
                    omfPath = ViewportOmfPath;
                    try
                    {
                        // dump the in-memory state so edits show up in the preview
                        SaveOMF(Main_OMF, omfPath);
                    }
                    catch (Exception exp)
                    {
                        viewportStatusLabel.Text = "OMF export failed";
                        Debug.WriteLine(exp.ToString());
                        return;
                    }
                }
            }

            viewportBusy = true;
            viewportStatusLabel.Text = string.IsNullOrEmpty(motion) ? "Building bind pose..." : "Building " + motion + "...";

            string glbPath = ViewportGlbPath;
            string tempPath = glbPath + ".tmp";
            ThreadPool.QueueUserWorkItem(delegate
            {
                int result = 0;
                string error = null;
                try
                {
                    if (omfPath != null)
                    {
                        result = ViewportLoadMotions(omfPath);
                        if (result != 0)
                            error = ViewportLastError();
                    }
                    if (result == 0)
                    {
                        // the whole motion as an animation - the viewer plays it
                        // and scrubs it on its own. Built aside and copied over,
                        // so it never gets to read a half written file
                        result = ViewportBuildGLBTime(motion, tempPath, -1);
                        if (result != 0)
                            error = ViewportLastError();
                        else
                            PublishViewportGlb(tempPath, glbPath);
                    }
                }
                catch (Exception exp)
                {
                    result = -100;
                    error = exp.Message;
                }

                int code = result;
                string message = error;
                try
                {
                    this.BeginInvoke((MethodInvoker)delegate { OnViewportModelBuilt(code, message, motion); });
                }
                catch (Exception) { }
            });
        }

        // Puts a freshly built preview where the viewer watches for it. The file
        // is written aside first and then copied over in one go: the viewer holds
        // an open handle on it for as long as it shows it, so it cannot be
        // swapped in - overwriting in place is the only thing that gets through.
        // Should the copy land mid reload, the viewer sees another change once it
        // is done and reads the file again.
        private static void PublishViewportGlb(string tempPath, string glbPath)
        {
            const int attempts = 40;
            for (int i = 0; ; ++i)
            {
                try
                {
                    File.Copy(tempPath, glbPath, true);
                    break;
                }
                catch (IOException)
                {
                    if (i >= attempts)
                        throw;
                    Thread.Sleep(25);
                }
            }

            try { File.Delete(tempPath); }
            catch (IOException) { }
        }

        private void OnViewportModelBuilt(int result, string error, string motion)
        {
            viewportBusy = false;

            if (result != 0)
            {
                viewportStatusLabel.Text = "Preview failed: " + (error ?? "unknown error");
                return;
            }

            string model = string.IsNullOrEmpty(viewportModelPath) ? "" : Path.GetFileName(viewportModelPath);
            string text = string.IsNullOrEmpty(motion) ? model + " - bind pose" : model + " - " + motion;

            // an OMF lists exactly the bones of the skeleton it was made for, so
            // differing counts mean the motions belong to another model
            int modelBones = ViewportGetModelBoneCount();
            int motionBones = ViewportGetMotionBoneCount();
            if (!string.IsNullOrEmpty(motion) && motionBones != 0 && motionBones != modelBones)
                text += string.Format("  [skeletons differ: model {0}, omf {1}]", modelBones, motionBones);

            viewportStatusLabel.Text = text;

            // a running viewer picks the new file up on its own
            if (!ViewerRunning)
                StartViewer();

            if (viewportRestartPending)
            {
                viewportRestartPending = false;
                RequestViewportUpdate(true);
            }
        }


        // ---- the f3d process -------------------------------------------------

        private bool ViewerRunning
        {
            get
            {
                try
                {
                    return viewportStarted && viewerProcess != null && !viewerProcess.HasExited;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        // Old f3d builds know neither option, so the help text decides.
        private string ViewportHelpText()
        {
            if (viewportHelpText != null)
                return viewportHelpText;

            viewportHelpText = "";
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(ViewportExePath, "--help");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.WorkingDirectory = Path.GetDirectoryName(ViewportExePath);
                using (Process probe = Process.Start(info))
                {
                    viewportHelpText = probe.StandardOutput.ReadToEnd();
                    probe.WaitForExit(10000);
                }
            }
            catch (Exception) { }

            return viewportHelpText;
        }

        private bool ViewportAutoPlaySupported()
        {
            return ViewportHelpText().Contains("animation-autoplay");
        }

        // Without it the viewer cannot pick up a rebuilt preview at all.
        private bool ViewportWatchSupported()
        {
            return ViewportHelpText().Contains("--watch");
        }

        // The viewer reports itself idle well before its window exists - it has
        // a scene to import and a GL context to bring up first - so the handle
        // is waited for rather than read once, or the window would be left
        // hanging outside the editor.
        private IntPtr WaitForViewerWindow()
        {
            for (int waited = 0; waited < 10000; waited += 25)
            {
                if (viewerProcess.HasExited)
                    return IntPtr.Zero;

                viewerProcess.Refresh();
                IntPtr handle = viewerProcess.MainWindowHandle;
                if (handle != IntPtr.Zero)
                    return handle;

                Thread.Sleep(25);
            }
            return IntPtr.Zero;
        }

        // Brings up the viewer on the preview file. It stays up for the rest of
        // the session: --watch makes it reload the file whenever we rewrite it.
        private void StartViewer()
        {
            if (!viewportEnabled || !File.Exists(ViewportExePath) || !File.Exists(ViewportGlbPath))
                return;

            StopViewer();

            try
            {
                string arguments = "\"" + ViewportGlbPath + "\"";
                if (ViewportWatchSupported())
                {
                    arguments += " --watch";
                    // the shipped config turns this on, and together with --watch
                    // it is a trap: a preview caught half written fails to load,
                    // the file is dropped from the group and nothing we write
                    // afterwards is ever looked at again
                    arguments += " --remove-empty-file-groups=false";
                    // the shipped config puts the file name on screen, and the
                    // preview file is always the same one - nothing to read there
                    arguments += " --filename=false";
                    // X-Ray textures are dark to begin with and the viewer lights
                    // them for a neutral scene, so the model needs a good deal
                    // more light than the default to read at all
                    arguments += " --light-intensity=6";
                    // and no ambient light off the default environment map
                    arguments += " --hdri-ambient=false";

                    // the frame rate is deliberately left alone: the animation
                    // advances 1/frame-rate per turn of the event loop, so
                    // asking for more than the loop can deliver does not make it
                    // smoother, it makes it play slow

                    // the viewer draws a bar over the animation with the time it
                    // is really at, and in the newer builds that bar can be
                    // dragged - which is the scrubber of this viewport. The
                    // 3.5.0 release only knows the plain flag
                    arguments += ViewportHelpText().Contains("\"advanced\"")
                        ? " --animation-progress=advanced"
                        : " --animation-progress=true";
                }
                if (viewportAutoPlayItem.Checked && ViewportAutoPlaySupported())
                    arguments += " --animation-autoplay";

                viewerProcess = new Process();
                viewerProcess.StartInfo.FileName = ViewportExePath;
                viewerProcess.StartInfo.Arguments = arguments;
                viewerProcess.StartInfo.UseShellExecute = false;
                viewerProcess.StartInfo.WindowStyle = ProcessWindowStyle.Minimized;
                viewerProcess.StartInfo.WorkingDirectory = ViewportAppFolder;
                viewerProcess.EnableRaisingEvents = true;
                viewerProcess.Exited += ViewerProcessExited;
                viewerProcess.Start();
                viewerProcess.WaitForInputIdle(10000);

                viewerWindow = WaitForViewerWindow();
                if (viewerWindow == IntPtr.Zero)
                {
                    viewportStarted = false;
                    viewportStatusLabel.Text = "The viewer window did not show up";
                    return;
                }

                viewportStarted = true;

                if (viewportTornOff)
                {
                    // a viewer restarted while torn off stays torn off
                    ApplyViewportTearOff(true);
                }
                else
                {
                    SetParent(viewerWindow, viewportHost.Handle);
                    int style = GetWindowLong(viewerWindow, GWL_STYLE);
                    style = style & ~WS_CAPTION & ~WS_THICKFRAME;
                    SetWindowLong(viewerWindow, GWL_STYLE, style);
                    ResizeEmbeddedViewer();
                }
            }
            catch (Exception exp)
            {
                viewportStarted = false;
                viewportStatusLabel.Text = "Can't start the viewer";
                Debug.WriteLine(exp.ToString());
            }
        }

        private void StopViewer()
        {
            viewportStarted = false;
            viewerWindow = IntPtr.Zero;
            if (viewerProcess == null)
                return;

            viewportStoppingViewer = true;	// the exit below is not the user's doing
            try
            {
                if (!viewerProcess.HasExited)
                    viewerProcess.Kill();
                // Kill only asks for the end, it does not wait for it, and until
                // the viewer is really gone it still holds the preview open - the
                // cleanup that follows would then leave the file behind
                viewerProcess.WaitForExit(3000);
                viewerProcess.Close();
            }
            catch (Exception) { }
            viewerProcess = null;
            viewportStoppingViewer = false;
        }

        private void ViewerProcessExited(object sender, EventArgs e)
        {
            if (viewportStoppingViewer)
                return;
            try
            {
                this.BeginInvoke((MethodInvoker)delegate { OnViewerClosed(); });
            }
            catch (Exception) { }
        }

        // Closing the torn off window is how one says the viewport is not wanted
        // any more - there is nothing left to show, so the viewport goes off.
        private void OnViewerClosed()
        {
            if (viewportStoppingViewer || !viewportEnabled)
                return;

            viewportStarted = false;
            viewerWindow = IntPtr.Zero;
            viewportTornOff = false;	// next time it opens in the editor again
            UpdateViewportTearButton();

            viewportShowItem.Checked = false;
            EnableViewport(false);
            viewportSettings.Write("ViewportEnabled", "0");
        }

        private void ViewportReloadClick(object sender, EventArgs e)
        {
            RequestViewportUpdate(true);
        }

        // Autoplay is settled when the viewer starts, so a running one is
        // brought up again to hear about the change - the preview file it
        // watches would not carry it.
        private void ViewportAutoPlayClick(object sender, EventArgs e)
        {
            viewportSettings.Write("ViewportAutoPlay", viewportAutoPlayItem.Checked ? "1" : "0");

            if (ViewerRunning)
                StartViewer();
        }
    }
}
