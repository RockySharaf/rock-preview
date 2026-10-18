using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace Anything.Preview.UI;

/// <summary>Virtual-mode, read-only grid for spreadsheets, CSV, archive and folder listings.</summary>
internal sealed class TableView : UserControl
{
    private readonly Label _caption = new() { Dock = DockStyle.Top, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0), Visible = false };
    private readonly Grid _grid = new();
    private TableData? _data;

    private sealed class Grid : DataGridView
    {
        public Grid()
        {
            DoubleBuffered = true;
            Dock = DockStyle.Fill;
            VirtualMode = true;
            ReadOnly = true;
            AllowUserToAddRows = false;
            AllowUserToDeleteRows = false;
            AllowUserToResizeRows = false;
            AllowUserToOrderColumns = false;
            BorderStyle = BorderStyle.None;
            BackgroundColor = SystemColors.Window;
            SelectionMode = DataGridViewSelectionMode.CellSelect;
            RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.DisableResizing;
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
            EnableHeadersVisualStyles = false;
            ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 240, 240);
            RowHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 240, 240);
            DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            GridColor = Color.FromArgb(225, 225, 225);
            RowTemplate.Height = 20;
        }
    }

    public TableView()
    {
        Controls.Add(_grid);
        Controls.Add(_caption);
        _grid.CellValueNeeded += (_, e) =>
        {
            if (_data == null || e.RowIndex < 0 || e.RowIndex >= _data.Rows.Count) return;
            var row = _data.Rows[e.RowIndex];
            e.Value = e.ColumnIndex < row.Length ? row[e.ColumnIndex] : "";
        };
        _grid.RowPostPaint += (_, e) =>
        {
            if (_data?.ShowRowNumbers != true) return;
            var r = new Rectangle(e.RowBounds.Left, e.RowBounds.Top, _grid.RowHeadersWidth - 4, e.RowBounds.Height);
            TextRenderer.DrawText(e.Graphics, (e.RowIndex + 1).ToString(), _grid.Font, r, Color.Gray,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
    }

    public void SetData(TableData? data)
    {
        _data = data;
        _grid.SuspendLayout();
        _grid.RowCount = 0;
        _grid.Columns.Clear();
        if (data != null)
        {
            _grid.RowHeadersVisible = data.ShowRowNumbers;
            _grid.RowHeadersWidth = Math.Max(40, TextRenderer.MeasureText(data.Rows.Count.ToString(), _grid.Font).Width + 18);
            for (int c = 0; c < data.Columns.Count; c++)
            {
                int w = TextRenderer.MeasureText(data.Columns[c], _grid.Font).Width + 16;
                for (int r = 0; r < Math.Min(60, data.Rows.Count); r++)
                {
                    var row = data.Rows[r];
                    if (c < row.Length && row[c].Length > 0)
                        w = Math.Max(w, TextRenderer.MeasureText(row[c].Length > 80 ? row[c][..80] : row[c], _grid.Font).Width + 12);
                }
                var col = new DataGridViewTextBoxColumn
                {
                    HeaderText = data.Columns[c], Width = Math.Clamp(w, 40, 420), SortMode = DataGridViewColumnSortMode.NotSortable,
                    FillWeight = 1,
                };
                _grid.Columns.Add(col);
            }
            _grid.RowCount = data.Rows.Count;
            _grid.CurrentCell = null; // no current-row glyph over the row numbers
            string cap = data.Caption ?? "";
            if (data.Truncated) cap += (cap.Length > 0 ? "  -  " : "") + "showing the first " + data.Rows.Count.ToString("N0") + " rows";
            _caption.Text = cap;
            _caption.Visible = cap.Length > 0;
        }
        _grid.ResumeLayout();
    }
}

/// <summary>Simple documents (docx/odt/pptx text, RTF) in a read-only RichTextBox.</summary>
internal sealed class DocumentView : RichTextBox
{
    private const int MaxChars = 400_000;

    public DocumentView()
    {
        ReadOnly = true;
        BorderStyle = BorderStyle.None;
        BackColor = SystemColors.Window;
        DetectUrls = false;
        ShortcutsEnabled = true;
        WordWrap = true;
        ScrollBars = RichTextBoxScrollBars.Vertical;
    }

    public void SetDocument(PreviewResult r)
    {
        try
        {
            if (r.Rtf != null) { Rtf = r.Rtf; return; }
            Rtf = BuildRtf(r.Document ?? Array.Empty<DocBlock>());
        }
        catch
        {
            Text = r.Rtf ?? string.Join(Environment.NewLine, (r.Document ?? Array.Empty<DocBlock>()).Select(b => b.Text));
        }
    }

    internal static string BuildRtf(IReadOnlyList<DocBlock> blocks)
    {
        var sb = new StringBuilder();
        sb.Append(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fswiss Segoe UI;}}{\colortbl;\red40\green40\blue40;\red31\green78\blue121;\red110\green110\blue110;}");
        sb.Append(@"\viewkind4\uc1\f0\fs20\cf1\sa120 ");
        int chars = 0;
        foreach (var b in blocks)
        {
            if (chars > MaxChars) { sb.Append(@"\i (truncated)\i0\par "); break; }
            chars += b.Text.Length;
            switch (b.Style)
            {
                case DocBlockStyle.Title: sb.Append(@"\pard\sa200\fs40\b\cf2 "); break;
                case DocBlockStyle.Heading1: sb.Append(@"\pard\sb240\sa120\fs32\b\cf2 "); break;
                case DocBlockStyle.Heading2: sb.Append(@"\pard\sb200\sa100\fs26\b\cf2 "); break;
                case DocBlockStyle.Heading3: sb.Append(@"\pard\sb160\sa80\fs22\b\cf2 "); break;
                case DocBlockStyle.ListItem: sb.Append(@"\pard\li360\fi-200\sa60\fs20\cf1 \bullet  "); break;
                case DocBlockStyle.Caption: sb.Append(@"\pard\sa120\fs18\i\cf3 "); break;
                default: sb.Append(@"\pard\sa120\fs20\cf1 "); break;
            }
            Escape(sb, b.Text);
            sb.Append(@"\b0\i0\par").Append('\n');
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static void Escape(StringBuilder sb, string s)
    {
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '{': sb.Append(@"\{"); break;
                case '}': sb.Append(@"\}"); break;
                case '\n': sb.Append(@"\line "); break;
                case '\r': break;
                case '\t': sb.Append(@"\tab "); break;
                default:
                    if (c < 0x80) sb.Append(c);
                    else sb.Append(@"\u").Append((short)c).Append('?');
                    break;
            }
        }
    }
}

/// <summary>Info card: big icon/thumbnail + facts, then a hex dump (binary files) or the folder listing.</summary>
internal sealed class InfoView : UserControl
{
    private readonly HeaderPanel _header = new() { Dock = DockStyle.Top };
    private readonly TextView _hex = new() { Dock = DockStyle.Fill, ShowLineNumbers = false, Visible = false };
    private readonly TableView _table = new() { Dock = DockStyle.Fill, Visible = false };

    public InfoView()
    {
        BackColor = SystemColors.Window;
        Controls.Add(_hex);
        Controls.Add(_table);
        Controls.Add(_header);
    }

    public void SetResult(PreviewResult r)
    {
        _header.Set(r);
        _hex.Visible = r.HexDump != null;
        if (r.HexDump != null) _hex.SetText(r.HexDump);
        // Note: Control.Visible reads false while the parent is hidden, so use the computed flag.
        bool showTable = r.HexDump == null && r.Table != null;
        _table.Visible = showTable;
        _table.SetData(showTable ? r.Table : null);
    }

    public void Clear() => _header.Set(null);

    private sealed class HeaderPanel : Control
    {
        private Bitmap? _icon; // not owned
        private string _title = "";
        private string _sub = "";
        private List<KeyValuePair<string, string>> _meta = new();
        private List<string> _diag = new();

        public HeaderPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = SystemColors.Window;
            Height = 160;
        }

        public void Set(PreviewResult? r)
        {
            _icon = r?.Image;
            _title = r == null ? "" : (Path.GetFileName(r.Path) is { Length: > 0 } n ? n : r.Path);
            _sub = r?.FormatName ?? "";
            _meta = r?.Metadata ?? new();
            _diag = r?.Diagnostics ?? new();
            int lines = 2 + _meta.Count + (_diag.Count > 0 ? 1 + _diag.Count : 0);
            Height = Math.Max(Math.Min(_icon?.Height ?? 0, 128) + 24, 20 + lines * (Font.Height + 4) + 12);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            int x = 16, y = 12;
            if (_icon != null)
            {
                int s = Math.Min(128, Math.Max(_icon.Width, _icon.Height));
                var fit = Util.BitmapUtil.FitSize(_icon.Size, new Size(s, s), allowUpscale: true);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(_icon, new Rectangle(x + (128 - fit.Width) / 2, y + (128 - fit.Height) / 2, fit.Width, fit.Height));
                x += 128 + 20;
            }
            int w = Math.Max(50, ClientSize.Width - x - 12);
            using (var bold = new Font(Font.FontFamily, Font.Size + 3, FontStyle.Bold))
            {
                TextRenderer.DrawText(g, _title, bold, new Rectangle(x, y, w, bold.Height + 2), ForeColor, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                y += bold.Height + 2;
            }
            TextRenderer.DrawText(g, _sub, Font, new Rectangle(x, y, w, Font.Height + 2), Color.DimGray, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            y += Font.Height + 10;
            int keyW = 0;
            foreach (var kv in _meta) keyW = Math.Max(keyW, TextRenderer.MeasureText(kv.Key, Font).Width);
            keyW = Math.Min(keyW + 8, 140);
            foreach (var kv in _meta)
            {
                TextRenderer.DrawText(g, kv.Key, Font, new Rectangle(x, y, keyW, Font.Height + 2), Color.Gray, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, kv.Value, Font, new Rectangle(x + keyW, y, w - keyW, Font.Height + 2), ForeColor, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                y += Font.Height + 4;
            }
            if (_diag.Count > 0)
            {
                y += 4;
                TextRenderer.DrawText(g, "Providers tried:", Font, new Point(x, y), Color.Gray, TextFormatFlags.NoPrefix);
                y += Font.Height + 2;
                foreach (var d in _diag)
                {
                    TextRenderer.DrawText(g, d, Font, new Rectangle(x, y, w, Font.Height + 2), Color.Firebrick, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    y += Font.Height + 2;
                }
            }
        }
    }
}
