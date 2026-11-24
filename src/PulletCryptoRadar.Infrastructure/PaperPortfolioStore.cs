using System.Text.Json;
using PulletCryptoRadar.Core;

namespace PulletCryptoRadar.Infrastructure;

public sealed partial class FoundationStore
{
    public PaperAudit AuditPaper()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred:true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Payload FROM V2PaperPortfolio WHERE Id=1";
        var portfolio = JsonSerializer.Deserialize<PaperPortfolio>((string)command.ExecuteScalar()!)!;
        command.CommandText = "SELECT Payload FROM V2PaperAccount WHERE Id=1";
        var account = JsonSerializer.Deserialize<PaperAccount>((string)command.ExecuteScalar()!)!;
        command.CommandText = "SELECT Payload FROM V2PaperLedger ORDER BY At,EventId";
        var events = new List<PaperLedgerEvent>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) events.Add(JsonSerializer.Deserialize<PaperLedgerEvent>(reader.GetString(0))!);
        transaction.Commit();
        return new(portfolio,account,events);
    }

    public PaperPortfolio Portfolio()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM V2PaperPortfolio WHERE Id=1";
        return JsonSerializer.Deserialize<PaperPortfolio>((string)command.ExecuteScalar()!)!;
    }

    public PaperTransition CommitPaper(Guid commandId,Func<PaperPortfolio,PaperTransition> apply)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("Paper command ID is required.");
        lock (writer)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT Payload FROM V2PaperCommand WHERE CommandId=$id";
            command.Parameters.AddWithValue("$id",commandId.ToString());
            if (command.ExecuteScalar() is string cached) return JsonSerializer.Deserialize<PaperTransition>(cached)!;
            command.Parameters.Clear();
            command.CommandText = "SELECT Payload FROM V2PaperPortfolio WHERE Id=1";
            var before = JsonSerializer.Deserialize<PaperPortfolio>((string)command.ExecuteScalar()!)!;
            var transition = apply(before);
            var after = transition.Portfolio;
            if (after.AccountId != before.AccountId || after.InitialCash != before.InitialCash ||
                after.Balance != before.Balance+transition.Events.Sum(item => item.CashDelta) ||
                after.Revision < before.Revision || after.Revision > before.Revision+1 ||
                transition.Events.Any(item => item.CashDelta != item.RealizedPnl-item.Fee+item.Funding || item.Fee < 0) ||
                after.Positions.Count(item => item.RemainingQuantity > 0) > 6 ||
                after.Positions.Any(item => item.Leverage is < 1 or > 10 || item.InitialMargin is <= 0 or > 100 ||
                    item.InitialQuantity <= 0 || item.RemainingQuantity < 0 || item.RemainingQuantity > item.InitialQuantity))
                throw new InvalidDataException("Paper ledger invariants violated.");
            if (after.UpdatedAt < before.UpdatedAt || after.Positions.Select(item => item.PositionId).Distinct().Count() != after.Positions.Count ||
                transition.Events.Any(item => item.Quantity < 0 || item.At > after.UpdatedAt ||
                    !after.Positions.Any(position => position.PositionId == item.PositionId)) ||
                transition.Events.Count > 0 && after.Revision != before.Revision+1 ||
                before.Positions.Any(item => !after.Positions.Any(position => position.PositionId == item.PositionId)))
                throw new InvalidDataException("Paper event chronology or identity violated.");
            foreach (var position in after.Positions)
            {
                var old = before.Positions.SingleOrDefault(item => item.PositionId == position.PositionId);
                var events = transition.Events.Where(item => item.PositionId == position.PositionId).ToArray();
                var expectedQuantity = (old?.RemainingQuantity ?? 0)+events.Where(item => item.Type == "Open").Sum(item => item.Quantity)-
                    events.Where(item => item.Type == "Reduce").Sum(item => item.Quantity);
                if (position.RemainingQuantity != expectedQuantity || position.RealizedPnl != (old?.RealizedPnl ?? 0)+events.Sum(item => item.RealizedPnl) ||
                    position.FeesPaid != (old?.FeesPaid ?? 0)+events.Sum(item => item.Fee) ||
                    position.FundingPnl != (old?.FundingPnl ?? 0)+events.Sum(item => item.Funding) ||
                    position.RemainingQuantity == 0 && position.ClosedAt is null)
                    throw new InvalidDataException("Paper position quantities or cashflows do not reconcile.");
                if (old is not null && (old.AssetId != position.AssetId || old.SetupId != position.SetupId || old.Side != position.Side ||
                    old.EntryPrice != position.EntryPrice || old.InitialQuantity != position.InitialQuantity || old.InitialMargin != position.InitialMargin ||
                    old.InitialPlannedRisk != position.InitialPlannedRisk || old.InitialStop != position.InitialStop || old.Costs != position.Costs ||
                    old.Rules != position.Rules || old.Leverage != position.Leverage || old.CorrelationGroup != position.CorrelationGroup ||
                    old.Side == TradeSide.Long && position.StopPrice < old.StopPrice || old.Side == TradeSide.Short && position.StopPrice > old.StopPrice))
                    throw new InvalidDataException("Paper entry risk is immutable and stops cannot widen.");
                if (old is null)
                {
                    command.Parameters.Clear();
                    command.CommandText = "SELECT COUNT(*) FROM V2Identity WHERE AssetId=$id";
                    command.Parameters.AddWithValue("$id",position.AssetId.ToString());
                    if ((long)command.ExecuteScalar()! != 1) throw new InvalidDataException("Paper asset identity is not registered.");
                }
            }
            foreach (var ledger in transition.Events)
            {
                command.Parameters.Clear();
                command.CommandText = "INSERT INTO V2PaperLedger VALUES($id,$position,$at,$payload)";
                command.Parameters.AddWithValue("$id",ledger.EventId.ToString());
                command.Parameters.AddWithValue("$position",ledger.PositionId.ToString());
                command.Parameters.AddWithValue("$at",ledger.At.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(ledger));
                command.ExecuteNonQuery();
            }
            command.Parameters.Clear();
            command.CommandText = "UPDATE V2PaperPortfolio SET Payload=$payload WHERE Id=1";
            command.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(after));
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = "SELECT Payload FROM V2PaperAccount WHERE Id=1";
            var account = JsonSerializer.Deserialize<PaperAccount>((string)command.ExecuteScalar()!)!;
            command.CommandText = "UPDATE V2PaperAccount SET Payload=$payload WHERE Id=1";
            command.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(account with { Cash = after.Balance }));
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO V2PaperCommand VALUES($id,$payload)";
            command.Parameters.AddWithValue("$id",commandId.ToString());
            command.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(transition));
            command.ExecuteNonQuery();
            transaction.Commit();
            return transition;
        }
    }

    public IReadOnlyList<PaperLedgerEvent> PaperEvents(Guid? positionId = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM V2PaperLedger WHERE ($id IS NULL OR PositionId=$id) ORDER BY At,EventId";
        command.Parameters.AddWithValue("$id",positionId is null ? DBNull.Value : positionId.ToString()!);
        using var reader = command.ExecuteReader();
        var result = new List<PaperLedgerEvent>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<PaperLedgerEvent>(reader.GetString(0))!);
        return result;
    }
}
