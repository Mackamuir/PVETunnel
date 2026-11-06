using System.Globalization;
using System.Text;

namespace PveTunnel.Relay;

/// <summary>Which way a stream was opened. Recorded on every stream-scoped log record.</summary>
public enum Direction
{
    /// <summary>Guest opened the stream; the relay dials the VM's configured egress target (GHOSTS).</summary>
    Egress,

    /// <summary>Controller connected to the VM's ingress port; the relay opens a stream to the guest (WinRM).</summary>
    Ingress,
}

/// <summary>
/// Single-line key=value logging for the relay.
/// </summary>
/// <remarks>
/// Every record carries <c>exercise=true</c> and <c>scope=out-of-scope</c>, which the
/// EXERCISE / OUT OF SCOPE visibility checklist requires of relay log fields and status output.
/// Anyone grepping node logs during an exercise must be able to see at a glance that this traffic
/// is range infrastructure and not something to investigate.
///
/// Values are escaped so a hostile VMID or target string cannot forge extra fields by embedding
/// spaces or quotes. Payload bytes are never logged, only counts.
/// </remarks>
public static class RelayLog
{
    public const string ExerciseNotice = "EXERCISE INFRASTRUCTURE - OUT OF SCOPE FOR BLUE TEAM";

    private static readonly object Gate = new();

    /// <summary>Sink for the formatted line. Replaceable so tests can capture without touching the console.</summary>
    public static Action<string> Sink { get; set; } = line => Console.Out.WriteLine(line);

    public static void Write(string @event, params (string Key, object? Value)[] fields)
    {
        var sb = new StringBuilder(160);
        sb.Append(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        sb.Append(" exercise=true scope=out-of-scope component=relay event=");
        Append(sb, @event);

        foreach (var (key, value) in fields)
        {
            if (value is null) continue;
            sb.Append(' ').Append(key).Append('=');
            Append(sb, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
        }

        var line = sb.ToString();
        // Console.Out is thread-safe, but the sink is replaceable and a test collector may not be.
        lock (Gate) Sink(line);
    }

    /// <summary>Quotes and escapes a value if it could otherwise break field parsing.</summary>
    private static void Append(StringBuilder sb, string raw)
    {
        var needsQuoting = raw.Length == 0;
        foreach (var c in raw)
        {
            if (c is ' ' or '"' or '\\' or '\n' or '\r' or '\t' || char.IsControl(c))
            {
                needsQuoting = true;
                break;
            }
        }

        if (!needsQuoting)
        {
            sb.Append(raw);
            return;
        }

        sb.Append('"');
        foreach (var c in raw)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c)) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
