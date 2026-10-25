using System.Text;

namespace Penghou.Zhinu;

internal static class ActivityGroupText
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Required(string value, int maximumBytes, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        // UTF-8 is at least one byte per UTF-16 code unit for valid strings.
        // Reject before scanning or encoding arbitrarily large caller values.
        if (value.Length > maximumBytes)
            throw new ArgumentException("Group metadata exceeds its text limit.", parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Any(char.IsControl))
            throw new ArgumentException("Group metadata exceeds its text limit or contains control characters.", parameterName);
        try
        {
            if (StrictUtf8.GetByteCount(value) > maximumBytes)
                throw new ArgumentException("Group metadata exceeds its UTF-8 byte limit.", parameterName);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Group metadata must contain valid Unicode.", parameterName);
        }
        return value;
    }

    internal static string SourceReference(string value)
    {
        Required(value, 1024, "sourceReference");
        // Plain relative display paths, never URLs, local roots or dereferenceable handles.
        if (value.IndexOfAny(['\\', ':', '%', '?', '#']) >= 0 ||
            value.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("Source references must be plain relative paths without traversal.", "sourceReference");
        }
        return value;
    }
}
