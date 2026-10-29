import hashlib, io, json, tempfile, unittest, zipfile
from pathlib import Path
from decimal import Decimal as D
import build_research_templates as research

def row(i,c=100,h=101,l=99,v=100):
    return {'t':1609459200+i*3600,'o':D(c),'h':D(h),'l':D(l),'c':D(c),'v':D(v),'q':D(v)*D(c),'tb':D(v)/2}

class ResearchTests(unittest.TestCase):
    def test_timestamp_and_ohlc_validation(self):
        def zipped(line):
            b=io.BytesIO()
            with zipfile.ZipFile(b,'w') as z:z.writestr('data.csv',line)
            return b.getvalue()
        bad=zipped('open_time,open,high,low,close,volume,close_time,quote_volume,count,taker_buy_volume,taker_buy_quote_volume,ignore\n1609459200000,100,101,99,100,10,1609462799999,1000,1,11,1100,0')
        with self.assertRaises(ValueError):research.parse_klines(bad,2000000000)
    def test_duplicate_identity_conflicts_are_rejected(self):
        with self.assertRaises(ValueError):research.combine([[row(1)],[row(1,c=100,h=102)]])
        self.assertEqual(1,len(research.combine([[row(1)],[row(1)]])))
    def test_future_outcomes_do_not_modify_breakout_signal_features(self):
        rows=[row(i) for i in range(140)];rows[80]=row(80,c=103,h=104,v=200)
        before=research.triggers(rows)[0]
        changed=rows.copy();changed[81]=row(81,c=103,h=1000,l=1)
        after=research.triggers(changed)[0]
        for key in ('time','volumeRatio','prior72hHigh','signalClose','confirmedSupportCandidate'):
            self.assertEqual(before[key],after[key])
        self.assertNotEqual(before['forward24hMaxHighPercent'],after['forward24hMaxHighPercent'])
    def test_missing_hours_do_not_create_continuous_triggers(self):
        rows=[row(i) for i in range(140)];rows[80]=row(80,c=103,h=104,v=200)
        self.assertEqual(1,len(research.triggers(rows)))
        rows.pop(79);self.assertEqual(0,len(research.triggers(rows)))
    def test_oi_uses_quantity_and_requires_temporally_close_observations(self):
        rows=[row(i) for i in range(200)];rows[100]=row(100,c=150,h=200,l=140)
        t=rows[100]['t']; metrics=[{'t':t-86400,'qty':D(100),'usd':D(1000)},{'t':t,'qty':D(80),'usd':D(3000)}]
        case=research.analyze_case({'symbol':'TESTUSDT','from':'2021-01','role':'historical'},rows,[],metrics,[],[])
        self.assertEqual(-20,case['oiQuantityChange24hAtPeakPercent'])
        self.assertIsNone(research.analyze_case({'symbol':'TESTUSDT','from':'2021-01','role':'historical'},rows,[],metrics[:1],[],[])['oiQuantityChange24hAtPeakPercent'])
    def test_peak_hour_low_is_not_claimed_as_post_peak_drawdown(self):
        rows=[row(i,c=100,h=101,l=99) for i in range(200)];rows[100]=row(100,c=150,h=200,l=1)
        case=research.analyze_case({'symbol':'TESTUSDT','from':'2021-01','role':'historical'},rows,[],[],[],[])
        self.assertEqual(-50.5,case['postPeakMinLowPercent']);self.assertTrue(case['unknownPeakBarSequence'])
    def test_fourteen_day_windows_are_elapsed_time_not_row_count(self):
        rows=[row(i) for i in range(700) if i not in range(140,190)];rows[100]=row(100,c=150,h=200,l=140)
        case=research.analyze_case({'symbol':'TESTUSDT','from':'2021-01','role':'historical'},rows,[],[],[],[])
        self.assertFalse(case['postPeak14dAvailable']);self.assertLessEqual(case['postEnd'],research.iso(rows[100]['t']+337*3600))
    def test_crash_context_anchors_near_crash_not_unrelated_earlier_peak(self):
        rows=[row(i) for i in range(500)];rows[1]=row(1,h=500);rows[490]=row(490,c=10,h=101,l=9);rows[480]=row(480,h=200);rows[490]["o"]=D(100)
        self.assertEqual(480,research.choose_peak({'role':'crash-context'},rows))
        self.assertEqual(1,research.choose_peak({'role':'historical'},rows))
        case=research.analyze_case({'symbol':'TESTUSDT','from':'2021-01','role':'crash-context'},rows,[],[],[],[])
        self.assertIn(research.iso(rows[490]['t']),[p['time'] for p in case['timeline']])
        self.assertEqual(research.iso(rows[490]['t']),case['eventTime'])
    def test_offline_cache_verifies_hash_before_use(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);a=research.Archive(root,True);(root/'file.zip').write_bytes(b'changed');a.entries['file.zip']={'status':'verified','sha256':hashlib.sha256(b'original').hexdigest()}
            with self.assertRaises(ValueError):a.get('file.zip')
    def test_funding_preserves_actual_interval_and_signed_settlement_rate(self):
        data=io.BytesIO()
        with zipfile.ZipFile(data,'w') as z:z.writestr('funding.csv','calc_time,funding_interval_hours,last_funding_rate\n1701388800000,4,-0.03\n')
        rows=research.parse_funding(data.getvalue());self.assertEqual(4,rows[0]['hours']);self.assertEqual(D('-.03'),rows[0]['rate'])
    def test_metric_symbol_mismatch_cannot_mix_contracts(self):
        data=io.BytesIO()
        with zipfile.ZipFile(data,'w') as z:z.writestr('metrics.csv','create_time,symbol,sum_open_interest,sum_open_interest_value\n2023-12-31 00:00:00,OTHERUSDT,10,20\n')
        with self.assertRaises(ValueError):research.parse_metrics(data.getvalue(),'TRBUSDT')

if __name__=='__main__':unittest.main()
