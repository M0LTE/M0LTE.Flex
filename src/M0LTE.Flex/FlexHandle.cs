namespace M0LTE.Flex;

/// <summary>
/// Comparison for the radio's client handles, which arrive in two spellings: the connection
/// prologue's <c>H</c> line carries a bare hex handle, while status objects reference the same
/// handle in <c>0x…</c> form. Comparing them literally silently never matches, which on an
/// ownership check reads as "somebody else owns this" for our own slice.
/// </summary>
public static class FlexHandle
{
    /// <summary>The all-zero handle the radio reports for "no client", e.g.
    /// <c>interlock tx_client_handle=0x00000000</c> while receiving.</summary>
    public const string Unset = "00000000";

    /// <summary>Strips any <c>0x</c> prefix.</summary>
    public static string Normalize(string handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return handle.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? handle[2..] : handle;
    }

    /// <summary>Whether two handles name the same client. Empty handles never match, including
    /// each other: an absent handle is unknown, not a match.</summary>
    public static bool Matches(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        return Normalize(a).Equals(Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether the handle is the radio's "no client" value.</summary>
    public static bool IsUnset(string handle) =>
        string.IsNullOrEmpty(handle)
        || Normalize(handle).TrimStart('0').Length == 0;
}
