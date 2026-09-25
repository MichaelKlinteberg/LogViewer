namespace LogViewer.Core.IO;

/// <summary>A single physical line found in a byte buffer, in encoding-agnostic byte offsets.</summary>
public readonly record struct RawLine(long AbsoluteOffset, int LengthIncludingTerminator, int LengthExcludingTerminator, bool TerminatedByNewline);

/// <summary>
/// Splits a byte stream into physical lines using the newline byte pattern for a given encoding,
/// without ever materializing more than the current chunk in memory. Handles '\n' and '\r\n'
/// (the trailing '\r', if present as the encoded unit right before the newline, is stripped from
/// <see cref="RawLine.LengthExcludingTerminator"/>).
/// </summary>
public static class LineSplitter
{
    /// <summary>
    /// Scans <paramref name="buffer"/> (representing file bytes starting at <paramref name="bufferAbsoluteOffset"/>)
    /// for complete lines. Returns the lines found; a trailing incomplete line (no terminator yet) is NOT
    /// returned and its length is reported via <paramref name="incompleteTailLength"/> so the caller can
    /// re-read from (bufferAbsoluteOffset + buffer.Length - incompleteTailLength) next time.
    /// </summary>
    public static List<RawLine> SplitComplete(ReadOnlySpan<byte> buffer, long bufferAbsoluteOffset, byte[] newLineBytes, int unitSize, out int incompleteTailLength)
    {
        var results = new List<RawLine>();
        int searchStart = 0;
        int consumed = 0;

        while (true)
        {
            int idx = IndexOf(buffer, searchStart, newLineBytes);
            if (idx < 0)
            {
                break;
            }

            int lineEndExclusive = idx; // start of the newline sequence
            int terminatorLength = newLineBytes.Length;
            int lineStart = consumed;
            int lengthIncludingTerminator = idx + terminatorLength - lineStart;

            int lengthExcludingTerminator = lineEndExclusive - lineStart;
            // Strip a preceding '\r' unit (CRLF) if present.
            if (lengthExcludingTerminator >= unitSize)
            {
                var crBytes = unitSize == 2 ? IsCrUnitBigEndianAware(buffer, lineEndExclusive - unitSize, newLineBytes) : buffer[lineEndExclusive - 1] == 0x0D;
                if (crBytes)
                {
                    lengthExcludingTerminator -= unitSize;
                }
            }

            results.Add(new RawLine(bufferAbsoluteOffset + lineStart, lengthIncludingTerminator, lengthExcludingTerminator, true));

            consumed = idx + terminatorLength;
            searchStart = consumed;
        }

        incompleteTailLength = buffer.Length - consumed;
        return results;
    }

    private static bool IsCrUnitBigEndianAware(ReadOnlySpan<byte> buffer, int unitStart, byte[] newLineBytes)
    {
        // newLineBytes tells us which byte in the 2-byte unit holds the ASCII value (LE: low byte first, BE: high byte first).
        bool littleEndian = newLineBytes[0] == 0x0A;
        byte asciiByte = littleEndian ? buffer[unitStart] : buffer[unitStart + 1];
        byte otherByte = littleEndian ? buffer[unitStart + 1] : buffer[unitStart];
        return asciiByte == 0x0D && otherByte == 0x00;
    }

    private static int IndexOf(ReadOnlySpan<byte> buffer, int start, byte[] pattern)
    {
        if (start >= buffer.Length)
        {
            return -1;
        }

        var slice = buffer[start..];
        int found = slice.IndexOf(pattern);
        return found < 0 ? -1 : found + start;
    }
}
