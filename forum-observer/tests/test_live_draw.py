import unittest
from datetime import datetime
from zoneinfo import ZoneInfo
from forum_observer.live_draw import normalize_live_draws


class LiveLedgerResultTests(unittest.TestCase):
    def test_only_actual_nonfallback_current_issue_with_consistent_zodiac(self):
        # 49 maps to the lunar-year zodiac, independently of all other numbers.
        valid={'issue':2026286,'openTime':'2026-10-13 21:35:31',
               'numbers':['01','02','03','04','05','06','49'],
               'special':'49','yearPet':'马','specialZodiac':'马'}
        now=datetime(2026,10,13,22,0,tzinfo=ZoneInfo('Asia/Shanghai'))
        records=normalize_live_draws({'records':[valid]},now)
        self.assertEqual(len(records),1)
        self.assertEqual(records[0]['special_zodiac'],'马')
        self.assertEqual(records[0]['actual_source'],'smart-ledger:/api/results')
        self.assertEqual(normalize_live_draws({'records':[
            dict(valid,specialZodiac='虎')]},now),[])
        self.assertEqual(normalize_live_draws({'records':[
            dict(valid,openTime='2026-10-13 22:35:31')]},now),[])

    def test_fallback_and_missing_results_are_rejected(self):
        with self.assertRaises(ValueError):
            normalize_live_draws({'fallback':True,'records':[{}]})
        with self.assertRaises(ValueError):
            normalize_live_draws({'records':[]})


if __name__=='__main__':
    unittest.main()
