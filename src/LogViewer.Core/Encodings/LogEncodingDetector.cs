namespace LogViewer.Core.Encodings;

/// <summary>
/// Result of sniffing the byte-order-mark (or lack thereof) at the start of a file.
/// </summary>
public sealed record DetectedEncoding(System.Text.Encoding Encoding, int PreambleLength);

/// <summary>
/// Detects the text encoding of a log file by inspecting the leading bytes (BOM).
/// Falls back to UTF-8 without BOM when no marker is present, which also correctly
/// decodes plain ASCII text used by most Windows log producers.
///
/// Limitation: files without a BOM that are actually encoded in a legacy code page
/// (e.g. Windows-1252) are not statistically detected; they are decoded as UTF-8 and
/// invalid byte sequences are replaced with the Unicode replacement character. This is
/// documented in the README.
/// </summary>
public static class LogEncodingDetector
{
    public static DetectedEncoding Detect(byte[] head, int length)
    {
        if (length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
        {
            return new DetectedEncoding(new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 3);
        }

        if (length >= 2 && head[0] == 0xFF && head[1] == 0xFE)
        {
            // UTF-16 LE. (Not distinguishing UTF-32 LE which would also start 0xFF 0xFE 0x00 0x00 - extremely rare for logs.)
            return new DetectedEncoding(System.Text.Encoding.Unicode, 2);
        }

        if (length >= 2 && head[0] == 0xFE && head[1] == 0xFF)
        {
            return new DetectedEncoding(System.Text.Encoding.BigEndianUnicode, 2);
        }

        return new DetectedEncoding(new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 0);
    }

    /// <summary>Byte pattern that represents '\n' (0x0A) in the given encoding.</summary>
    public static byte[] NewLineBytes(System.Text.Encoding encoding)
    {
        if (encoding.Equals(System.Text.Encoding.Unicode))
        {
            return new byte[] { 0x0A, 0x00 };
        }

        if (encoding.Equals(System.Text.Encoding.BigEndianUnicode))
        {
            return new byte[] { 0x00, 0x0A };
        }

        return new byte[] { 0x0A };
    }

    /// <summary>Number of bytes used to encode one "unit" for alignment/newline scanning (1 for UTF-8/ASCII, 2 for UTF-16).</summary>
    public static int UnitSize(System.Text.Encoding encoding) =>
        encoding.Equals(System.Text.Encoding.Unicode) || encoding.Equals(System.Text.Encoding.BigEndianUnicode) ? 2 : 1;
}
