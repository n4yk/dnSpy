﻿/*
    Copyright (C) 2014-2016 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.Text.Editor;
using dnSpy.Decompiler.Shared;
using dnSpy.Shared.Text;
using ICSharpCode.AvalonEdit.Document;

namespace dnSpy.Text.Editor {
	sealed class LogEditor : ILogEditor {
		public object UIObject => wpfTextView.UIObject;
		public IInputElement FocusedElement => wpfTextView.FocusedElement;
		public FrameworkElement ScaleElement => wpfTextView.ScaleElement;
		public object Tag { get; set; }

		public bool WordWrap {
			get { return (wpfTextView.Options.GetOptionValue(DefaultTextViewOptions.WordWrapStyleId) & WordWrapStyles.WordWrap) != 0; }
			set {
				if (value)
					wpfTextView.Options.SetOptionValue(DefaultTextViewOptions.WordWrapStyleId, WordWrapStyles.WordWrap | WordWrapStyles.AutoIndent);
				else
					wpfTextView.Options.SetOptionValue(DefaultTextViewOptions.WordWrapStyleId, WordWrapStyles.None);
			}
		}

		public bool ShowLineNumbers {
			get { return wpfTextView.Options.GetOptionValue(DefaultTextViewHostOptions.LineNumberMarginId); }
			set {
				wpfTextView.Options.SetOptionValue(DefaultTextViewHostOptions.LineNumberMarginId, value);
				UpdatePaddingElement();
			}
		}

		void UpdatePaddingElement() => wpfTextView.Options.SetOptionValue(DefaultTextViewHostOptions.SelectionMarginId, !ShowLineNumbers);

		readonly IWpfTextView wpfTextView;
		readonly DnSpyTextEditor textEditor;
		readonly CachedColorsList cachedColorsList;
		readonly Dispatcher dispatcher;
		CachedTextTokenColors cachedTextTokenColors;

		sealed class GuidObjectsCreator : IGuidObjectsCreator {
			readonly LogEditor logEditorUI;

			public GuidObjectsCreator(LogEditor logEditorUI) {
				this.logEditorUI = logEditorUI;
			}

			public IEnumerable<GuidObject> GetGuidObjects(GuidObjectsCreatorArgs args) {
				yield return new GuidObject(MenuConstants.GUIDOBJ_LOG_EDITOR_GUID, logEditorUI);
			}
		}

		public LogEditor(LogEditorOptions options, ITextEditorFactoryService2 textEditorFactoryService2) {
			this.dispatcher = Dispatcher.CurrentDispatcher;
			options = options ?? new LogEditorOptions();

			var wpfTextView = textEditorFactoryService2.CreateTextView(null, options, (object)options.ContentType ?? options.ContentTypeGuid, () => new GuidObjectsCreator(this));
			wpfTextView.Options.SetOptionValue(DefaultTextViewHostOptions.LineNumberMarginId, false);
			wpfTextView.Options.SetOptionValue(DefaultTextViewOptions.DragDropEditingId, false);
			wpfTextView.Options.SetOptionValue(DefaultTextViewOptions.ViewProhibitUserInputId, true);
			wpfTextView.Options.SetOptionValue(DefaultTextViewOptions.OverwriteModeId, true);
			wpfTextView.Options.SetOptionValue(DefaultTextViewHostOptions.GlyphMarginId, false);
			wpfTextView.Options.SetOptionValue(DefaultTextViewOptions.WordWrapStyleId, WordWrapStyles.None);
			this.wpfTextView = wpfTextView;
			this.textEditor = wpfTextView.DnSpyTextEditor;
			this.cachedColorsList = new CachedColorsList();
			this.textEditor.AddColorizer(new CachedColorsListColorizer(cachedColorsList, ColorPriority.Default));
			SetNewDocument();
			// Setting IsReadOnly to true doesn't mean it's readonly since undo and redo still work.
			// Fix that by removing the commands.
			Remove(this.textEditor.TextArea.CommandBindings, ApplicationCommands.Undo);
			Remove(this.textEditor.TextArea.CommandBindings, ApplicationCommands.Redo);
			UpdatePaddingElement();
		}

		static void Remove(CommandBindingCollection bindings, ICommand cmd) {
			for (int i = bindings.Count - 1; i >= 0; i--) {
				var b = bindings[i];
				if (b.Command == cmd)
					bindings.RemoveAt(i);
			}
		}

		void SetNewDocument() {
			cachedTextTokenColors = new CachedTextTokenColors();
			textEditor.Document = new TextDocument();
			cachedColorsList.Clear();
			cachedColorsList.Add(0, cachedTextTokenColors);
		}

		public void Clear() {
			ClearPendingOutput();
			SetNewDocument();
		}

		public string GetText() => wpfTextView.TextSnapshot.GetText();
		public void Write(string text, object color) => OutputPrint(text, color);
		public void Write(string text, OutputColor color) => OutputPrint(text, color.Box());
		public void WriteLine(string text, OutputColor color) => WriteLine(text, color.Box());

		public void WriteLine(string text, object color) {
			OutputPrint(text, color);
			OutputPrint(Environment.NewLine, color);
		}

		public void Write(IEnumerable<ColorAndText> text) {
			var list = text as IList<ColorAndText>;
			if (list == null)
				list = text.ToArray();
			if (list.Count == 0)
				return;

			lock (pendingOutputLock)
				pendingOutput.AddRange(list);

			FlushOutput();
		}

		void OutputPrint(string text, object color, bool startOnNewLine = false) {
			if (string.IsNullOrEmpty(text))
				return;

			lock (pendingOutputLock) {
				if (startOnNewLine) {
					if (pendingOutput.Count > 0) {
						var last = pendingOutput[pendingOutput.Count - 1];
						if (last.Text.Length > 0 && last.Text[last.Text.Length - 1] != '\n')
							pendingOutput.Add(new ColorAndText(BoxedOutputColor.Text, Environment.NewLine));
					}
					else if (LastLine.Length != 0)
						pendingOutput.Add(new ColorAndText(BoxedOutputColor.Text, Environment.NewLine));
				}
				pendingOutput.Add(new ColorAndText(color, text));
			}

			FlushOutput();
		}

		ITextSnapshotLine LastLine {
			get {
				var line = wpfTextView.TextSnapshot.GetLineFromLineNumber(wpfTextView.TextSnapshot.LineCount - 1);
				Debug.Assert(line.Length == line.LengthIncludingLineBreak);
				return line;
			}
		}

		void RawAppend(string text) => wpfTextView.TextBuffer.Insert(wpfTextView.TextSnapshot.Length, text);
		int CaretOffset => wpfTextView.Caret.Position.BufferPosition.Position;

		void FlushOutputUIThread() {
			dispatcher.VerifyAccess();

			var currentLine = wpfTextView.TextSnapshot.GetLineFromPosition(CaretOffset);
			bool canMoveCaret = currentLine.Start == LastLine.Start;

			ColorAndText[] newPendingOutput;
			var sb = new StringBuilder();
			lock (pendingOutputLock) {
				pendingOutput_dispatching = false;
				newPendingOutput = pendingOutput.ToArray();
				pendingOutput.Clear();
			}

			foreach (var info in newPendingOutput) {
				sb.Append(info.Text);
				cachedTextTokenColors.Append(info.Color, info.Text);
			}
			if (sb.Length == 0)
				return;

			cachedTextTokenColors.Flush();
			RawAppend(sb.ToString());

			if (canMoveCaret) {
				wpfTextView.Caret.MoveTo(new SnapshotPoint(wpfTextView.TextSnapshot, wpfTextView.TextSnapshot.Length));
				wpfTextView.Caret.EnsureVisible();
			}
		}

		void FlushOutput() {
			if (!dispatcher.CheckAccess()) {
				lock (pendingOutputLock) {
					if (pendingOutput_dispatching)
						return;
					pendingOutput_dispatching = true;
					try {
						dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(FlushOutputUIThread));
					}
					catch {
						pendingOutput_dispatching = false;
						throw;
					}
				}
			}
			else
				FlushOutputUIThread();
		}
		readonly object pendingOutputLock = new object();
		List<ColorAndText> pendingOutput = new List<ColorAndText>();
		bool pendingOutput_dispatching;

		void ClearPendingOutput() {
			lock (pendingOutputLock) {
				pendingOutput = new List<ColorAndText>();
				pendingOutput_dispatching = false;
			}
		}

		public void Dispose() {
			if (!wpfTextView.IsClosed)
				wpfTextView.Close();
		}
	}
}