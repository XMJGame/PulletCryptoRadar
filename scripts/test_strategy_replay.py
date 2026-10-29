import copy, unittest
from replay_strategy_research import features, oi_feature, plan, settle, step, liquidation_price, replay, RULES, H

def bars(n=340):
    return [{'t':i*H,'o':100.,'h':102.,'l':98.,'c':100.,'v':100.,'q':10000.,'tb':50.} for i in range(n)]

def position():
    return {'entry':100.,'qty':10.,'initialQty':10.,'margin':100.,'stop':96.,'targets':[105.,110.],
      'hit':[False,False],'gross':0.,'fees':.5,'funding':0.,'adjustment':0.,'returns':0.,'exits':[]}

class ReplayTests(unittest.TestCase):
    def test_future_candles_cannot_change_features(self):
        a=bars();a[330]['l']=97.;a[336]['v']=150.;x=features(a,336)
        self.assertIsNotNone(x)
        a[337].update(c=1e9,h=1e10,l=.001,v=1e10)
        self.assertEqual(x,features(a,336))

    def test_gap_invalidates_feature_window(self):
        a=bars();a[336]['t']+=H;self.assertIsNone(features(a,336))

    def test_unconfirmed_pivot_is_not_support(self):
        a=bars();a[330]['l']=97.;a[335]['l']=90.
        self.assertEqual(97.,features(a,336)['support'])

    def test_oi_lag_freshness_quantity_and_future(self):
        t=100*H;old=t-300-24*H;now=t-300
        data=[{'t':old,'qty':100.,'usd':1e6},{'t':now,'qty':110.,'usd':1e3},{'t':t,'qty':9999.,'usd':1.}]
        self.assertAlmostEqual(.1,oi_feature(data,t)['change'])
        self.assertIsNone(oi_feature(data,t+1201))
        self.assertIsNone(oi_feature(data[1:],t))

    def test_structural_stop_sizes_risk_not_full_margin(self):
        f={'stop':95.,'atr':2.,'pressures':[106.,115.]};p,e=plan(f,100.,1000.,1000.)
        self.assertIsNone(e);self.assertLess(p['margin'],100);self.assertLessEqual(p['estimatedLossWithBuffer'],10.000001)
        self.assertEqual(['confirmed_pressure','confirmed_pressure'],p['targetBasis'])

    def test_liquidation_conflict_is_rejected(self):
        p,e=plan({'stop':85.,'atr':2.,'pressures':[]},100.,1000.,1000.)
        self.assertIsNone(p);self.assertEqual('stop_liquidation_conflict',e)

    def test_stop_wins_when_hour_contains_stop_and_target(self):
        p=position();step(p,{'o':100.,'l':95.,'h':120.},None,0)
        self.assertEqual(0,p['qty']);self.assertEqual('structure_stop',p['exits'][0]['reason'])

    def test_liquidation_stress_precedes_stop(self):
        p=position();self.assertGreater(liquidation_price(p),90.)
        step(p,{'o':100.,'l':80.,'h':120.},None,0)
        self.assertEqual('liquidation_stress',p['exits'][0]['reason']);self.assertEqual(0,p['returns'])

    def test_partial_exits_leave_forty_percent(self):
        p=position();step(p,{'o':100.,'l':99.,'h':115.},None,0)
        self.assertAlmostEqual(4.,p['qty']);self.assertAlmostEqual(40.,p['margin'])
        self.assertEqual([True,True],p['hit'])

    def test_gap_stop_uses_open_and_costs(self):
        p=position();step(p,{'o':94.,'l':93.,'h':95.},None,0)
        self.assertLess(p['exits'][0]['price'],94.)

    def test_actual_mark_low_used_for_liquidation_stress(self):
        p=position();step(p,{'o':100.,'l':99.,'h':101.},{'l':80.},0)
        self.assertEqual('liquidation_stress',p['exits'][0]['reason'])

    def test_partial_and_signed_funding_reconcile(self):
        p=position();p['margin']+=2;p['funding']=-2
        settle(p,3,105,'first',0);settle(p,7,99,'close',H)
        net=p['returns']-100-.5
        self.assertAlmostEqual(net,p['gross']-p['fees']-p['funding']+p['adjustment'])

    def test_portfolio_caps_and_next_open_with_no_same_bar_trail(self):
        data={}
        for s in ('AAA','BBB','CCC'):
            rows=bars(5);rows[1].update(o=101.,h=115.,l=100.,c=101.)
            f={'a':True,'b':True,'decision':H,'oi':None,'atr':2.,'stop':96.,'pressures':[107.,113.],'volumeRatio':2.}
            data[s]={'rows':rows,'lookup':{r['t']:r for r in rows},'features':{H:f},
                'fundingTimes':[0],'funding':{0:{'t':0,'rate':.001}},'marks':{}}
        r=replay(data,'a',False,0,5*H)
        self.assertEqual(2,r['trades']);self.assertEqual(1,r['skipped']['two_position_limit'])
        for p in r['ledger']:
            self.assertAlmostEqual(101.*1.0005,p['entry'])
            self.assertEqual(['partial_1','partial_2'],[e['reason'] for e in p['exits'][:2]])
        self.assertLessEqual(r['maximumMarginInUse'],200.)

    def test_actual_signed_funding_only_charged_while_position_is_open(self):
        rows=bars(4)
        f={'a':True,'b':False,'decision':H,'oi':None,'atr':2.,'stop':96.,'pressures':[107.,113.],'volumeRatio':2.}
        d={'rows':rows,'lookup':{r['t']:r for r in rows},'features':{H:f},'fundingTimes':[0,H,2*H],
            'funding':{0:{'rate':.01},H:{'rate':.5},2*H:{'rate':-.01}},'marks':{}}
        result=replay({'AAA':d},'a',False,0,4*H);p=result['ledger'][0]
        self.assertAlmostEqual(-p['initialQty'],p['funding'])
        self.assertEqual(result['finalCash'],result['equity'][-1]['equity'])

    def test_missing_path_exits_at_first_resumed_open_not_previous_close(self):
        rows=[r for r in bars(4) if r['t']!=2*H];rows[-1].update(o=90.,h=91.,l=89.,c=90.)
        f={'a':True,'b':False,'decision':H,'oi':None,'atr':2.,'stop':96.,'pressures':[107.,113.],'volumeRatio':2.}
        d={'rows':rows,'lookup':{r['t']:r for r in rows},'features':{H:f},'fundingTimes':[0],
            'funding':{0:{'rate':0}},'marks':{}}
        r=replay({'AAA':d},'a',False,0,4*H);p=r['ledger'][0]
        self.assertTrue(p['incompletePath']);self.assertEqual('gap_resume_exit',p['exits'][0]['reason'])
        self.assertLess(p['exits'][0]['price'],90.)

    def test_predeclared_symbol_window_end_releases_position_on_time(self):
        rows=bars(5);f={'a':True,'b':False,'decision':H,'oi':None,'atr':2.,'stop':96.,'pressures':[107.,113.],'volumeRatio':2.}
        d={'rows':rows,'lookup':{r['t']:r for r in rows},'features':{H:f},'fundingTimes':[0],
            'funding':{0:{'rate':0}},'marks':{},'periodEnds':[2*H]}
        r=replay({'AAA':d},'a',False,0,5*H);p=r['ledger'][0]
        self.assertEqual('window_end_exit',p['exits'][0]['reason'])
        self.assertEqual(1.,p['durationHours']);self.assertFalse(p['incompletePath'])

    def test_terminal_missing_data_has_unknown_fill_not_backdated_close(self):
        rows=bars(2);f={'a':True,'b':False,'decision':H,'oi':None,'atr':2.,'stop':96.,'pressures':[107.,113.],'volumeRatio':2.}
        d={'rows':rows,'lookup':{r['t']:r for r in rows},'features':{H:f},'fundingTimes':[0],
            'funding':{0:{'rate':0}},'marks':{},'periodEnds':[3*H]}
        r=replay({'AAA':d},'a',False,0,5*H);p=r['ledger'][0]
        self.assertTrue(p['incompletePath']);self.assertIsNone(p['exits'][0]['price'])
        self.assertEqual('data_missing_collateral_stress',p['exits'][0]['reason'])
        self.assertAlmostEqual(-p['initialMargin']-p['entryFee'],p['net'])

if __name__=='__main__':unittest.main()
