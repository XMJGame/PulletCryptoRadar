"""Read-only review of the persisted virtual account; never replays or creates fills."""
import argparse
import collections
import datetime as dt
import gzip
import json
import pathlib
import sqlite3


def review(database):
    def decode(blob):
        return json.loads(gzip.decompress(blob))

    def timestamp(value):
        return dt.datetime.fromisoformat(value)

    with sqlite3.connect(database.resolve().as_uri() + '?mode=ro', uri=True) as connection:
        connection.execute('BEGIN')
        state = decode(connection.execute('SELECT Payload FROM PaperAccount WHERE Id=1').fetchone()[0])
        start = timestamp(state['ExecutionStartedAt'])
        end = timestamp(state['UpdatedAt'])
        audits = [decode(row[0]) for row in connection.execute('SELECT Payload FROM PaperAudit ORDER BY Time')]
        signals = {signal['Id']: signal for audit in audits for signal in audit.get('Signals', [])
                   if start <= timestamp(signal['DecisionAt']) <= end}
        trades = [decode(row[0]) for row in connection.execute('SELECT Payload FROM PaperTrade ORDER BY ClosedAt')]
        snapshots = [decode(row[0]) for row in connection.execute('SELECT Payload FROM PaperHoldingSnapshot ORDER BY Time')]
    rows = list(signals.values())
    recent = [row for row in rows if timestamp(row['DecisionAt']) >= end - dt.timedelta(hours=24)]
    candidates = [row for row in rows if row['Status'] == 'Candidate']
    positions = []
    for position_id in dict.fromkeys(row['PositionId'] for row in snapshots):
        samples = [row for row in snapshots if row['PositionId'] == position_id]
        pnl = [row['MarkPnl'] for row in samples if row['MarkPnl'] is not None]
        gaps = [{'from': a['CapturedAt'], 'to': b['CapturedAt'],
                 'seconds': (timestamp(b['CapturedAt']) - timestamp(a['CapturedAt'])).total_seconds()}
                for a, b in zip(samples, samples[1:])
                if timestamp(b['CapturedAt']) - timestamp(a['CapturedAt']) > dt.timedelta(minutes=3)]
        positions.append({'positionId': position_id, 'symbol': samples[0]['Position']['Symbol'],
                          'samples': len(samples), 'freshSamples': sum(row['DataFresh'] for row in samples),
                          'sampledMinGrossPnl': min(pnl) if pnl else None,
                          'sampledMaxGrossPnl': max(pnl) if pnl else None, 'gapsOver3Minutes': gaps})
    return {'from': start.isoformat(), 'asOf': end.isoformat(), 'enabled': state['Enabled'],
            'rulesVersion': state['Rules']['Version'], 'cash': state['Cash'],
            'positions': state['Positions'], 'closedTrades': trades,
            'realizedPnl': sum(row['NetPnl'] for row in trades),
            'analysis': {'total': len(rows), 'statuses': dict(collections.Counter(row['Status'] for row in rows)),
                         'reasons': dict(collections.Counter(row['Reason'] for row in rows)),
                         'last24h': {'total': len(recent), 'statuses': dict(collections.Counter(row['Status'] for row in recent))},
                         'readySymbols': sorted({row['Symbol'] for row in rows if row['Status'] != 'Insufficient'}),
                         'candidates': candidates,
                         'candidatesOutsideOiRange': sum(row['Oi24hPercent'] is None or not 0 <= row['Oi24hPercent'] <= 30 for row in candidates)},
            'holdingObservations': positions,
            'limitations': ['Repeated hourly A/B and directional analyses are not independent trades.',
                            'Sampled gross PnL excludes fees/funding and cannot describe prices during gaps.',
                            'No retroactive fills, capital reset or strategy parameter changes.']}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--database', type=pathlib.Path, default=pathlib.Path('artifacts/win-x64/Data/PulletCryptoRadar.db'))
    parser.add_argument('--output', type=pathlib.Path, default=pathlib.Path('artifacts/paper-account-review.json'))
    args = parser.parse_args()
    result = review(args.database)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({key: result[key] for key in ['from', 'asOf', 'cash', 'realizedPnl']}, ensure_ascii=False))
    print(json.dumps({key: result['analysis'][key] for key in ['total', 'statuses', 'last24h', 'readySymbols', 'candidatesOutsideOiRange']}, ensure_ascii=False))
