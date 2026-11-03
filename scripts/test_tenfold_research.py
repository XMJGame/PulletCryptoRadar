import unittest
from research_tenfold import observations,HOUR

class TenfoldLabelTests(unittest.TestCase):
    def rows(self):
        return [dict(t=i*HOUR,o=1,h=1,l=1,c=1,v=100) for i in range(1057)]
    def test_high_wick_is_not_closed_hour_tenfold(self):
        rows=self.rows();rows[400].update(h=11,c=9)
        r=observations('TEST',rows)[0]
        self.assertTrue(r['complete30d']);self.assertTrue(r['highTouch10x']);self.assertFalse(r['closedHour10x'])
    def test_gap_and_short_tail_are_unknown(self):
        rows=self.rows();del rows[500]
        r=observations('TEST',rows)[0]
        self.assertFalse(r['complete30d']);self.assertIsNone(r['closedHour10x'])
    def test_future_high_never_changes_predictor_features(self):
        rows=self.rows();before=observations('TEST',rows)[0];rows[400].update(h=20,c=12);after=observations('TEST',rows)[0]
        self.assertTrue(after['closedHour10x'])
        for k in ['range72hPercent','volumeRatio','return24hPercent','return7dPercent']:
            self.assertEqual(before[k],after[k])
        self.assertEqual(1,after['future30dTerminalMultiple'])

if __name__=='__main__':unittest.main()
