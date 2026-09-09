namespace CPT.Core.Agents;

/// <summary>Routes only explicitly linked sessions, suppressing duplicate and late events.</summary>
public sealed class OfficialEventGate
{
    private readonly HashSet<string> _finished = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public bool Accept(AgentProfile? agent, OfficialAppEvent item)
    {
        if (agent is null || !agent.ReceiveOfficialApp || agent.OfficialSessionId.Length == 0 || agent.OfficialSessionId != item.SessionId) return false;
        var key = item.SessionId + ":" + (item.TurnId.Length > 0 ? item.TurnId : item.Kind + ":" + item.Text);
        if (_finished.Contains(key)) return false;
        if (item.Kind is "Stop" or "Interrupt")
        {
            _finished.Add(key);
            _order.Enqueue(key);
            if (_order.Count > 1000) _finished.Remove(_order.Dequeue());
        }
        return true;
    }
}
