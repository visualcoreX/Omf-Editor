using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace OMF_Editor
{
    // Files dropped on the editor window. An .omf opens, or hands its motions
    // over to the file already open; .skl/.skls go the same way as the File menu
    // import; an .ogf becomes the model of the viewport. Anything else is not
    // taken, so the drop is refused before it happens.
    public partial class OMF_Editor
    {
        private enum DroppedOmfAnswer { Open, Merge, Cancel }

        // Called from the form constructor, once every control is in place.
        private void InitFileDrop()
        {
            AllowFileDrop(this);
        }

        // A drop lands on the control under the cursor and stops there: a child
        // that takes no drops of its own refuses it rather than passing it up to
        // the form. So the whole tree is registered, and so is whatever is added
        // to it later. AllowDrop doubles as the mark of an already registered
        // control - nothing else in the editor sets it.
        private void AllowFileDrop(Control control)
        {
            if (!control.AllowDrop)
            {
                control.AllowDrop = true;
                control.DragEnter += FileDragEnter;
                control.DragOver += FileDragEnter;
                control.DragDrop += FileDragDrop;
                control.ControlAdded += FileDropControlAdded;
            }

            foreach (Control child in control.Controls)
                AllowFileDrop(child);
        }

        private void FileDropControlAdded(object sender, ControlEventArgs e)
        {
            AllowFileDrop(e.Control);
        }

        private void FileDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = !OverViewport(e) && DroppedFiles(e.Data).Count != 0
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }

        private void FileDragDrop(object sender, DragEventArgs e)
        {
            if (OverViewport(e))
                return;

            List<string> files = DroppedFiles(e.Data);
            if (files.Count == 0)
                return;

            // the handler returns at once and the work - questions, converter
            // runs and all - happens after it: explorer stands still for as long
            // as the drop it started is being served
            BeginInvoke(new MethodInvoker(delegate { OpenDroppedFiles(files); }));
        }

        // A viewport with a model in it takes nothing. What is in it is the
        // viewer's window, and the file would have to go through it - it has
        // been told to leave dropped files alone, and the panel around it
        // refuses them as well rather than doing something the picture gives no
        // sign of. So the whole panel is one rectangle where the cursor says no.
        private bool OverViewport(DragEventArgs e)
        {
            if (viewportPanel == null || !viewportPanel.Visible)
                return false;

            // Except while it stands empty: then it is the Append OGF button
            // asking for a model, and a model dropped on it is the answer. The
            // button is up for exactly as long as there is no model, which is
            // also as long as there is no viewer window to get in the way.
            if (viewportAppendButton != null && viewportAppendButton.Visible)
                return false;

            // the drag carries screen coordinates, whatever control it is over
            return viewportPanel.RectangleToScreen(viewportPanel.ClientRectangle)
                .Contains(e.X, e.Y);
        }

        private static List<string> DroppedFiles(IDataObject data)
        {
            List<string> files = new List<string>();

            if (data == null || !data.GetDataPresent(DataFormats.FileDrop))
                return files;

            string[] paths = data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null)
                return files;

            foreach (string path in paths)
            {
                if (DroppedFileKind(path) != null)
                    files.Add(path);
            }
            return files;
        }

        // The extension in lower case for the three things the editor takes, and
        // null for everything else.
        private static string DroppedFileKind(string path)
        {
            string extension;
            try
            {
                extension = Path.GetExtension(path).ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                return null;		// not a path we can do anything with
            }

            switch (extension)
            {
                case ".omf":
                case ".skl":
                case ".skls":
                case ".ogf":
                    return extension;
                default:
                    return null;
            }
        }

        private void OpenDroppedFiles(List<string> files)
        {
            Activate();

            List<string> models = new List<string>();
            List<string> omfs = new List<string>();
            List<string> skls = new List<string>();

            foreach (string path in files)
            {
                switch (DroppedFileKind(path))
                {
                    case ".ogf": models.Add(path); break;
                    case ".omf": omfs.Add(path); break;
                    default: skls.Add(path); break;
                }
            }

            // the model goes in first: the motions that follow it are then built
            // into a preview right away instead of waiting for one
            if (models.Count != 0)
                LoadDroppedModel(models[0]);

            OpenDroppedOmfs(omfs);

            if (skls.Count != 0)
                ImportSklFiles(skls);
        }

        // Same as Viewport -> Load model..., without the file dialog: a model
        // dropped while the viewport is hidden brings it out.
        private void LoadDroppedModel(string path)
        {
            if (!viewportEnabled)
            {
                viewportShowItem.Checked = true;
                EnableViewport(true);
                if (!viewportEnabled)
                    return;
            }

            if (LoadViewportModel(path, true))
                RequestViewportUpdate(true);
        }

        private void OpenDroppedOmfs(List<string> paths)
        {
            foreach (string path in paths)
            {
                bool open = Main_OMF == null;

                if (!open)
                {
                    switch (AskDroppedOmf(path))
                    {
                        case DroppedOmfAnswer.Open: open = true; break;
                        case DroppedOmfAnswer.Merge: open = false; break;
                        default: return;
                    }
                }

                try
                {
                    if (open)
                        OpenFile(path);
                    else
                        AppendFile(path);
                }
                catch (Exception exp)
                {
                    MessageBox.Show(exp.ToString());
                }
            }
        }

        // With a file already open a dropped OMF can be either of two things:
        // the file to work on from now on, or motions to bring into the one
        // open. Both are wanted often enough that the drop asks which.
        private static DroppedOmfAnswer AskDroppedOmf(string path)
        {
            DialogResult answer = MessageBox.Show(
                "Open " + Path.GetFileName(path) + "?\n\n" +
                "No adds its motions to the file already open.",
                "Dropped OMF", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (answer == DialogResult.Yes)
                return DroppedOmfAnswer.Open;
            if (answer == DialogResult.No)
                return DroppedOmfAnswer.Merge;
            return DroppedOmfAnswer.Cancel;
        }
    }
}
