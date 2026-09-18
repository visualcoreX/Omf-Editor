using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace OMF_Editor
{
    // Finding a motion by name in a file that holds hundreds of them. Ctrl+F
    // puts a bar over the list; what is typed picks out the first motion whose
    // name carries it, Enter walks on to the next, and Ctrl+Enter takes every
    // match at once - which is how a whole family of motions gets deleted, saved
    // out or renamed in one go.
    //
    // The list is searched rather than filtered down to the matches: a motion is
    // addressed everywhere else by its place in it - the params panel, delete,
    // clone, the preview - so a shorter list would be a different file to all of
    // them. Nothing is hidden here, only pointed at.
    public partial class OMF_Editor
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        const int EM_SETCUEBANNER = 0x1501;

        // between the search box and the list, and the same again above it
        const int MotionSearchGap = 4;

        private Panel motionSearchBar;
        private TextBox motionSearchBox;
        private Label motionSearchCount;

        // where the list sits with the bar away, to give it back on Esc
        private Rectangle motionSearchListBounds;

        // the wildcard query the regex below was built for
        private string motionSearchPattern;
        private Regex motionSearchRegex;

        // Called from the form constructor.
        private void InitMotionSearch()
        {
            motionSearchBox = new TextBox();
            motionSearchBox.Name = "motionSearchBox";
            motionSearchBox.TextChanged += MotionSearchTextChanged;
            motionSearchBox.KeyDown += MotionSearchKeyDown;

            motionSearchCount = new Label();
            motionSearchCount.AutoSize = false;
            motionSearchCount.TextAlign = ContentAlignment.MiddleRight;
            motionSearchCount.ForeColor = SystemColors.GrayText;

            motionSearchBar = new Panel();
            motionSearchBar.Name = "motionSearchBar";
            motionSearchBar.Visible = false;	// laid out on the way up, over the list
            motionSearchBar.Controls.Add(motionSearchBox);
            motionSearchBar.Controls.Add(motionSearchCount);
            this.Controls.Add(motionSearchBar);

            ToolTip hint = new ToolTip();
            hint.SetToolTip(motionSearchBox,
                "Part of a motion name, or a pattern with * and ? matching the whole of it.\n" +
                "Enter - next match, Shift+Enter - previous, Ctrl+Enter - select them all,\n" +
                "Esc - close and go back to the list.");

            ToolStripMenuItem findItem = new ToolStripMenuItem("Find motion");
            findItem.ShortcutKeys = Keys.Control | Keys.F;
            findItem.Click += MotionSearchClick;

            // at the head of Tools, above the things that bring motions in: it is
            // about the file already open, and the only way to hear of Ctrl+F
            toolsToolStripMenuItem.DropDownItems.Insert(0, findItem);
            toolsToolStripMenuItem.DropDownItems.Insert(1, new ToolStripSeparator());

            // motions added, deleted or renamed behind an open bar change what
            // there is to find, so the counter is told to catch up
            bs.ListChanged += MotionSearchListChanged;
        }

        private void MotionSearchClick(object sender, EventArgs e)
        {
            ShowMotionSearch();
        }

        // Ctrl+F, from the menu shortcut. Coming back to an open bar takes the
        // text whole, so the next search is typed straight over the last one.
        private void ShowMotionSearch()
        {
            if (Main_OMF == null || motionSearchBar == null)
                return;

            if (!motionSearchBar.Visible)
            {
                motionSearchListBounds = lbxMotions.Bounds;

                int height = motionSearchBox.PreferredHeight + MotionSearchGap;
                motionSearchBar.SetBounds(motionSearchListBounds.X, motionSearchListBounds.Y,
                    motionSearchListBounds.Width, height);
                lbxMotions.SetBounds(motionSearchListBounds.X, motionSearchListBounds.Y + height,
                    motionSearchListBounds.Width, motionSearchListBounds.Height - height);

                LayoutMotionSearchBar();
                motionSearchBar.Visible = true;
                motionSearchBar.BringToFront();

                if (motionSearchBox.IsHandleCreated)
                {
                    SendMessage(motionSearchBox.Handle, EM_SETCUEBANNER, (IntPtr)1,
                        "Find motion");	// wParam 1: stays up while it is being typed into
                }
            }

            motionSearchBox.Focus();
            motionSearchBox.SelectAll();
            UpdateMotionSearchCount();
        }

        private void HideMotionSearch()
        {
            if (motionSearchBar == null || !motionSearchBar.Visible)
                return;

            motionSearchBar.Visible = false;
            lbxMotions.Bounds = motionSearchListBounds;
            lbxMotions.Focus();
        }

        private void LayoutMotionSearchBar()
        {
            int height = motionSearchBox.PreferredHeight;
            int counter = TextRenderer.MeasureText("8888 of 8888", motionSearchCount.Font).Width;
            int box = motionSearchBar.Width - counter - MotionSearchGap;
            if (box < 1)
                box = 1;

            motionSearchBox.SetBounds(0, 0, box, height);
            motionSearchCount.SetBounds(motionSearchBar.Width - counter, 0, counter, height);
        }

        private void MotionSearchTextChanged(object sender, EventArgs e)
        {
            // from where the list stands, so a name being typed out settles on
            // the one motion instead of walking down every longer name first
            FindMotion(1, true);
        }

        private void MotionSearchKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Escape:
                    HideMotionSearch();
                    break;
                case Keys.Enter:
                    if (e.Control)
                        SelectAllMotionMatches();
                    else
                        FindMotion(e.Shift ? -1 : 1, false);
                    break;
                case Keys.F3:
                    FindMotion(e.Shift ? -1 : 1, false);
                    break;
                case Keys.Down:
                    FindMotion(1, false);
                    break;
                case Keys.Up:
                    FindMotion(-1, false);
                    break;
                default:
                    return;
            }

            // none of these are text, and Enter in particular would ding
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private void MotionSearchListChanged(object sender, System.ComponentModel.ListChangedEventArgs e)
        {
            if (motionSearchBar == null || !motionSearchBar.Visible || !IsHandleCreated)
                return;

            // the list box is listening to the same event: the count is worth
            // reading once it has taken the change as well
            BeginInvoke(new MethodInvoker(UpdateMotionSearchCount));
        }

        // Walks the list from the selected motion in the given direction and
        // stops on the next name that matches, wrapping around the end. The
        // current motion is included while the query is being typed, so that a
        // match already selected is not stepped over.
        private void FindMotion(int direction, bool includeCurrent)
        {
            string query = motionSearchBox.Text.Trim();
            int count = lbxMotions.Items.Count;

            if (query.Length != 0 && count != 0)
            {
                int start = lbxMotions.SelectedIndex;
                if (start < 0)
                    start = direction > 0 ? 0 : count - 1;
                else if (!includeCurrent)
                    start += direction;

                for (int step = 0; step != count; ++step)
                {
                    int i = ((start + direction * step) % count + count) % count;
                    if (MotionMatches(i, query))
                    {
                        SelectOnlyMotion(i);
                        break;
                    }
                }
            }

            UpdateMotionSearchCount();
        }

        // Every match in one selection - what the context menu then works on.
        private void SelectAllMotionMatches()
        {
            string query = motionSearchBox.Text.Trim();
            if (query.Length == 0)
                return;

            int matches = 0;
            lbxMotions.BeginUpdate();
            lbxMotions.ClearSelected();
            for (int i = 0; i != lbxMotions.Items.Count; ++i)
            {
                if (MotionMatches(i, query))
                {
                    lbxMotions.SetSelected(i, true);
                    ++matches;
                }
            }
            lbxMotions.EndUpdate();

            SetMotionSearchCount(matches != 0 ? matches + " selected" : "not found", matches != 0);
        }

        private void SelectOnlyMotion(int index)
        {
            if (lbxMotions.SelectedIndices.Count == 1 && lbxMotions.SelectedIndex == index)
                return;

            lbxMotions.ClearSelected();
            lbxMotions.SelectedIndex = index;	// which also scrolls it into view
        }

        private void UpdateMotionSearchCount()
        {
            if (motionSearchBar == null || !motionSearchBar.Visible)
                return;

            string query = motionSearchBox.Text.Trim();
            if (query.Length == 0)
            {
                SetMotionSearchCount("", true);
                return;
            }

            int current = lbxMotions.SelectedIndex;
            int matches = 0;
            int position = 0;

            for (int i = 0; i != lbxMotions.Items.Count; ++i)
            {
                if (!MotionMatches(i, query))
                    continue;

                ++matches;
                if (i == current)
                    position = matches;
            }

            string text;
            if (matches == 0)
                text = "not found";
            else if (position != 0)
                text = position + " of " + matches;
            else
                text = matches + " found";

            SetMotionSearchCount(text, matches != 0);
        }

        private void SetMotionSearchCount(string text, bool found)
        {
            motionSearchCount.Text = text;
            // the box itself says when there is nothing, the way a browser does
            motionSearchBox.BackColor = found ? SystemColors.Window : Color.FromArgb(255, 205, 205);
        }

        private bool MotionMatches(int index, string query)
        {
            AnimationParams motion = lbxMotions.Items[index] as AnimationParams;
            if (motion == null || motion.Name == null)
                return false;

            if (query.IndexOf('*') < 0 && query.IndexOf('?') < 0)
                return motion.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

            return MotionSearchWildcard(query).IsMatch(motion.Name);
        }

        // A query with * or ? in it is a pattern over the whole name, the way
        // file names are matched - "*aim*_1" being the point of it.
        private Regex MotionSearchWildcard(string query)
        {
            if (motionSearchRegex == null || motionSearchPattern != query)
            {
                string pattern = "^" + Regex.Escape(query).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                motionSearchRegex = new Regex(pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                motionSearchPattern = query;
            }
            return motionSearchRegex;
        }
    }
}
