namespace PSDToolViewer.Core.Formats;

/// <summary>ファイル形式が不正・未対応の場合の例外。メッセージはそのまま利用者に表示する。</summary>
public sealed class DocumentFormatException(string message, Exception? inner = null) : Exception(message, inner);
