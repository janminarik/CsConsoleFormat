﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using Alba.CsConsoleFormat.Framework.Text;
using JetBrains.Annotations;

namespace Alba.CsConsoleFormat
{
    internal class InlineContainer : BlockElement
    {
        //private List<string> _actualStrings;
        private InlineSequence _inlineSequence;
        private List<List<InlineSegment>> _lines;

        public InlineContainer (BlockElement source)
        {
            DataContext = source.DataContext;
            TextAlign = source.TextAlign;
            TextWrap = source.TextWrap;
            Parent = source;
        }

        protected override Size MeasureOverride (Size availableSize)
        {
            if (availableSize.Width == 0)
                return new Size(0, 0);

            if (_inlineSequence == null) {
                _inlineSequence = new InlineSequence(this);
                foreach (InlineElement child in VisualChildren.Cast<InlineElement>())
                    child.GenerateSequence(_inlineSequence);
                _inlineSequence.ValidateStackSize();
            }
            _lines = new LineWrapper(this, availableSize).WrapSegments();
            return new Size(_lines.Select(GetLineLength).Max(), _lines.Count);
        }

        protected override Size ArrangeOverride (Size finalSize)
        {
            return finalSize;
        }

        public override void Render (ConsoleRenderBuffer buffer)
        {
            base.Render(buffer);
            ConsoleColor color = EffectiveColor, bgColor = EffectiveBgColor;
            for (int y = 0; y < _lines.Count; y++) {
                List<InlineSegment> line = _lines[y];
                int length = GetLineLength(line);

                int offset = 0;
                if (TextAlign == TextAlignment.Left || TextAlign == TextAlignment.Justify)
                    offset = 0;
                else if (TextAlign == TextAlignment.Center)
                    offset = (ActualWidth - length) / 2;
                else if (TextAlign == TextAlignment.Right)
                    offset = ActualWidth - length;

                int x = offset;
                foreach (InlineSegment segment in line) {
                    if (segment.Color != null)
                        color = segment.Color.Value;
                    if (segment.BgColor != null)
                        bgColor = segment.BgColor.Value;
                    if (segment.TextBuilder != null) {
                        string text = segment.ToString();
                        buffer.FillBackgroundRectangle(x, y, text.Length, 1, bgColor);
                        buffer.DrawString(x, y, color, text);
                        x += text.Length;
                    }
                }
            }
        }

        private static int GetLineLength (List<InlineSegment> line)
        {
            return line.Sum(s => s.TextLength);
        }

        private class LineWrapper
        {
            private readonly InlineContainer _container;
            private readonly Size _availableSize;
            private List<List<InlineSegment>> _lines;
            private List<InlineSegment> _curLine;
            private InlineSegment _curSeg;
            private int _segPos;
            private int _curLineLength;

            // Last possible wrap info:
            private int _wrapPos;
            private CharInfo _wrapChar;
            private int _wrapSegmentIndex;

            public LineWrapper (InlineContainer container, Size availableSize)
            {
                _container = container;
                _availableSize = availableSize;
                _wrapPos = -1;
            }

            [UsedImplicitly]
            private IEnumerable<object> DebugLines
            {
                get
                {
                    return _lines.Select(l => new {
                        text = string.Concat(l.Where(s => s.TextLength > 0).Select(s => s.ToString())),
                        len = GetLineLength(l),
                    });
                }
            }

            private int AvailableWidth
            {
                get { return _availableSize.Width; }
            }

            public List<List<InlineSegment>> WrapSegments ()
            {
                _curLine = new List<InlineSegment>();
                _lines = new List<List<InlineSegment>> { _curLine };

                foreach (InlineSegment sourceSeg in _container._inlineSequence.Segments) {
                    if (sourceSeg.Color != null || sourceSeg.BgColor != null) {
                        _curLine.Add(sourceSeg);
                    }
                    else {
                        TextWrapping textWrap = _container.TextWrap;
                        if (textWrap == TextWrapping.NoWrap)
                            AppendTextSegmentNoWrap(sourceSeg);
                        else if (textWrap == TextWrapping.CharWrap)
                            AppendTextSegmentCharWrap(sourceSeg);
                        else if (textWrap == TextWrapping.WordWrap)
                            AppendTextSegmentWordWrap(sourceSeg);
                    }
                }
                return _lines;
            }

            private void AppendTextSegmentNoWrap (InlineSegment sourceSeg)
            {
                _curSeg = InlineSegment.CreateWithBuilder(AvailableWidth);
                for (int i = 0; i < sourceSeg.Text.Length; i++) {
                    CharInfo c = CharInfo.From(sourceSeg.Text[i]);
                    if (c.IsNewLine)
                        StartNewLine();
                    else if (!c.IsZeroWidth)
                        _curSeg.TextBuilder.Append(c);
                }
                AppendCurrentSegment();
            }

            private void AppendTextSegmentCharWrap (InlineSegment sourceSeg)
            {
                _curSeg = InlineSegment.CreateWithBuilder(AvailableWidth);
                for (int i = 0; i < sourceSeg.Text.Length; i++) {
                    CharInfo c = CharInfo.From(sourceSeg.Text[i]);
                    if (!c.IsZeroWidth && _curSeg.TextLength >= AvailableWidth) {
                        // Proceed as if the current char is '\n', repeat with current char in the next iteration.
                        c = CharInfo.From('\n');
                        i--;
                    }
                    if (c.IsNewLine)
                        StartNewLine();
                    else if (!c.IsZeroWidth)
                        _curSeg.TextBuilder.Append(c);
                }
                AppendCurrentSegment();
            }

            private void AppendTextSegmentWordWrap (InlineSegment sourceSeg)
            {
                _curSeg = InlineSegment.CreateWithBuilder(AvailableWidth);
                _segPos = 0;
                for (int i = 0; i < sourceSeg.Text.Length; i++) {
                    CharInfo c = CharInfo.From(sourceSeg.Text[i]);
                    Debug.Assert(_curLineLength == GetLineLength(_curLine) + _curSeg.TextLength);
                    bool canAddChar = _curLineLength < AvailableWidth;
                    if (!canAddChar && !c.IsZeroWidth) {
                        // Proceed as if the current char is '\n', repeat with current char in the next iteration if not consumed.
                        if (_wrapPos == -1) {
                            if (!c.IsConsumedOnWrap) {
                                i--;
                                _curLineLength--;
                                _segPos--;
                            }
                            c = CharInfo.From('\n');
                        }
                        else if (!c.IsNewLine) {
                            AppendCurrentSegment();
                            WrapLine();
                        }
                    }
                    if (c.IsWrappable && (canAddChar || c.IsZeroWidth && !c.IsSoftHyphen)) {
                        _wrapPos = _segPos;
                        _wrapChar = c;
                        _wrapSegmentIndex = _curLine.Count;
                    }
                    if (c.IsNewLine) {
                        StartNewLine();
                    }
                    else if (!c.IsZeroWidth) {
                        _curSeg.TextBuilder.Append(c);
                        _curLineLength++;
                        _segPos++;
                    }
                }
                AppendCurrentSegment();
            }

            private void StartNewLine ()
            {
                if (_curSeg != null && _curSeg.TextLength > 0) {
                    _curLine.Add(_curSeg);
                    _curSeg = InlineSegment.CreateWithBuilder(AvailableWidth);
                }
                _curLine = new List<InlineSegment>();
                _lines.Add(_curLine);
                _curLineLength = 0;
                _wrapPos = -1;
                _segPos = 0;
            }

            private void WrapLine ()
            {
                string wrappedText = _curSeg.ToString(), textBeforeWrap, textAfterWrap;
                SplitWrappedText(wrappedText, out textBeforeWrap, out textAfterWrap);

                if (wrappedText != textBeforeWrap) {
                    InlineSegment wrappedSeg = _curLine[_wrapSegmentIndex];
                    wrappedSeg.TextBuilder.Clear();
                    wrappedSeg.TextBuilder.Append(textBeforeWrap);
                }

                List<InlineSegment> prevLine = _curLine;
                _curSeg = null;
                StartNewLine();

                _curLine.AddRange(prevLine.Skip(_wrapSegmentIndex + 1));
                _curLineLength = GetLineLength(_curLine);
                prevLine.RemoveRange(_wrapSegmentIndex + 1, prevLine.Count - _wrapSegmentIndex - 1);

                if (textAfterWrap != "") {
                    if (_curLineLength + textAfterWrap.Length <= AvailableWidth) {
                        InlineSegment nextSeg = InlineSegment.CreateWithBuilder(textAfterWrap.Length);
                        nextSeg.TextBuilder.Append(textAfterWrap);
                        _curLine.Add(nextSeg);
                        _curLineLength += textAfterWrap.Length;
                    }
                    else {
                        int lineOffset = _curLineLength;
                        for (int i = 0; i < textAfterWrap.Length; i += AvailableWidth) {
                            string nextSegText = textAfterWrap.SubstringSafe(i * AvailableWidth, AvailableWidth - lineOffset);
                            InlineSegment nextSeg = InlineSegment.CreateWithBuilder(nextSegText.Length);
                            nextSeg.TextBuilder.Append(nextSegText);
                            _curLine.Add(nextSeg);
                            if (nextSegText.Length >= AvailableWidth) {
                                StartNewLine();
                                lineOffset = 0;
                            }
                            else {
                                _curLineLength = nextSegText.Length;
                            }
                        }
                    }
                }

                _curSeg = InlineSegment.CreateWithBuilder(AvailableWidth);
            }

            private void SplitWrappedText (string wrappedText, out string textBeforeWrap, out string textAfterWrap)
            {
                if (_wrapChar.IsSpace) {
                    // "aaa bb" => "aaa" + "bb"
                    textBeforeWrap = wrappedText.Substring(0, _wrapPos);
                    textAfterWrap = wrappedText.Substring(_wrapPos + 1);
                }
                else if (_wrapChar.IsHyphen) {
                    // "aaa-bb" => "aaa-" + "bb"
                    textBeforeWrap = wrappedText.Substring(0, _wrapPos + 1);
                    textAfterWrap = wrappedText.Substring(_wrapPos + 1);
                }
                else if (_wrapChar.IsSoftHyphen) {
                    // "aaabb" => "aaa-" + "bb"
                    textBeforeWrap = wrappedText.Substring(0, _wrapPos) + "-";
                    textAfterWrap = wrappedText.Substring(_wrapPos);
                }
                else if (_wrapChar.IsZeroWidthSpace) {
                    // "aaabb" => "aaa" + "bb"
                    textBeforeWrap = wrappedText.Substring(0, _wrapPos);
                    textAfterWrap = wrappedText.Substring(_wrapPos);
                }
                else
                    throw new InvalidOperationException();
            }

            private void AppendCurrentSegment ()
            {
                if (_curSeg.TextLength > 0)
                    _curLine.Add(_curSeg);
            }
        }

        private class InlineSequence : IInlineSequence
        {
            private readonly Stack<InlineSegment> _formattingStack = new Stack<InlineSegment>();

            public List<InlineSegment> Segments { get; private set; }

            public InlineSequence (InlineContainer container)
            {
                var initSegment = new InlineSegment {
                    Color = container.EffectiveColor,
                    BgColor = container.EffectiveBgColor,
                };
                _formattingStack.Push(initSegment);
                Segments = new List<InlineSegment>();
                AddFormattingSegment();
            }

            public void AppendText (string text)
            {
                Segments.Add(InlineSegment.CreateFromText(text));
            }

            public void PushColor (ConsoleColor color)
            {
                _formattingStack.Push(InlineSegment.CreateFromColors(color, null));
                AddFormattingSegment();
            }

            public void PushBgColor (ConsoleColor bgColor)
            {
                _formattingStack.Push(InlineSegment.CreateFromColors(null, bgColor));
                AddFormattingSegment();
            }

            public void PopFormatting ()
            {
                _formattingStack.Pop();
                AddFormattingSegment();
            }

            public void ValidateStackSize ()
            {
                if (_formattingStack.Count != 1)
                    throw new InvalidOperationException("Push and Pop calls during inline generation must be balanced.");
            }

            [SuppressMessage ("ReSharper", "PossibleInvalidOperationException", Justification = "Value is guaranteed not be not null.")]
            private void AddFormattingSegment ()
            {
                InlineSegment lastSegment = Segments.LastOrDefault();
                if (lastSegment == null || lastSegment.Text != null) {
                    lastSegment = new InlineSegment();
                    Segments.Add(lastSegment);
                }
                lastSegment.Color = _formattingStack.First(s => s.Color != null).Color.Value;
                lastSegment.BgColor = _formattingStack.First(s => s.BgColor != null).BgColor.Value;
            }
        }

        private class InlineSegment
        {
            public ConsoleColor? Color { get; set; }
            public ConsoleColor? BgColor { get; set; }
            public string Text { get; private set; }
            public StringBuilder TextBuilder { get; private set; }

            public int TextLength
            {
                get { return TextBuilder != null ? TextBuilder.Length : Text != null ? Text.Length : 0; }
            }

            public static InlineSegment CreateFromColors (ConsoleColor? color, ConsoleColor? bgColor)
            {
                return new InlineSegment { Color = color, BgColor = bgColor };
            }

            public static InlineSegment CreateFromText (string text)
            {
                return new InlineSegment { Text = text != null ? text.Replace("\r", "") : "" };
            }

            public static InlineSegment CreateWithBuilder (int length)
            {
                return new InlineSegment { TextBuilder = new StringBuilder(length) };
            }

            public override string ToString ()
            {
                if (TextBuilder != null)
                    return TextBuilder.ToString();
                else if (Text != null)
                    return Text;
                else
                    return (Color != null ? Color.ToString() : "null") + " " + (BgColor != null ? BgColor.ToString() : "null");
            }
        }

        private struct CharInfo
        {
            private readonly char _c;

            private CharInfo (char c)
            {
                _c = c;
            }

            public bool IsHyphen
            {
                get { return _c == '-'; }
            }

            public bool IsNewLine
            {
                get { return _c == '\n'; }
            }

            public bool IsSoftHyphen
            {
                get { return _c == Chars.SoftHyphen; }
            }

            public bool IsSpace
            {
                get { return _c == ' '; }
            }

            public bool IsConsumedOnWrap
            {
                get { return _c == ' ' || _c == '\n' || _c == Chars.ZeroWidthSpace; }
            }

            public bool IsWrappable
            {
                get { return _c == ' ' || _c == '-' || _c == Chars.SoftHyphen || _c == Chars.ZeroWidthSpace; }
            }

            public bool IsZeroWidth
            {
                get { return _c == Chars.SoftHyphen || _c == Chars.ZeroWidthSpace; }
            }

            public bool IsZeroWidthSpace
            {
                get { return _c == Chars.ZeroWidthSpace; }
            }

            public static CharInfo From (char c)
            {
                return new CharInfo(c);
            }

            public static implicit operator char (CharInfo self)
            {
                return self._c;
            }

            public override string ToString ()
            {
                return _c.ToString();
            }
        }
    }
}