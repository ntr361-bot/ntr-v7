import tempfile,time,unittest
from unittest.mock import patch,MagicMock
from pathlib import Path
from forum_observer.image_queue import ImageQueue,reconcile_passes


class ImageQueueTests(unittest.TestCase):
    def test_download_once_and_cache_used_by_both_ocr_passes(self):
        with tempfile.TemporaryDirectory() as folder:
            q=ImageQueue(Path(folder)/'state.json',Path(folder)/'cache')
            response=MagicMock();response.__enter__.return_value.read.return_value=b'\x89PNGtest'
            completed=MagicMock(returncode=0,stdout='六肖：鼠牛虎兔龙蛇'.encode(),stderr=b'')
            row=dict(url='https://jmz.chunshengsh.com/tk118files/comment/test.png',attempts=0)
            with patch('forum_observer.image_queue.urlopen',return_value=response) as download,patch('forum_observer.image_queue.shutil.which',return_value='tesseract'),patch('forum_observer.image_queue.subprocess.run',return_value=completed) as ocr:
                first=q.worker(row);second=q.worker(row)
            self.assertEqual(download.call_count,1)
            self.assertEqual(ocr.call_count,4)
            self.assertEqual(first['status'],'ocr_complete')
            self.assertEqual(first['passes'],second['passes'])

    def test_deadline_keeps_pending_and_next_run_resumes_without_duplicates(self):
        with tempfile.TemporaryDirectory() as folder:
            state=Path(folder)/'state.json';cache=Path(folder)/'cache'
            q=ImageQueue(state,cache);keys=[q.enqueue('https://a/1'),q.enqueue('https://a/1'),q.enqueue('https://a/2')]
            q.process(keys,0)
            self.assertEqual(len(q.jobs),2)
            self.assertTrue(all(x['status']=='pending' for x in q.jobs.values()))
            calls=[]
            def worker(row):calls.append(row['url']);return dict(row,status='ocr_complete',passes={'6':'文字','11':'文字'})
            q=ImageQueue(state,cache);q.process(keys,time.monotonic()+10,worker=worker)
            self.assertEqual(len(calls),2)
            q=ImageQueue(state,cache);q.process(keys,time.monotonic()+10,worker=worker)
            self.assertEqual(len(calls),2)

    def test_short_group_does_not_discard_agreed_six(self):
        a='284期六肖：鼠牛虎兔龙蛇\n284期三肖：鼠牛虎'
        r=reconcile_passes(a,a,'2026284')
        self.assertEqual(len(r['accepted_groups']),1)
        self.assertEqual(r['accepted_groups'][0]['picks'],list('鼠牛虎兔龙蛇'))
        self.assertTrue(r['rejected_groups'])

    def test_context_period_and_history_and_mismatch(self):
        a='六肖：鼠牛虎兔龙蛇'
        self.assertEqual(reconcile_passes(a,a,'2026284')['ocr'],'')
        self.assertTrue(reconcile_passes(a,a,'2026284',True)['ocr'])
        b='283期六肖：鼠牛虎兔龙蛇'
        self.assertEqual(reconcile_passes(b,b,'2026284',True)['ocr'],'')
        self.assertEqual(reconcile_passes('上期杀马41','上期杀马41','2026284',True)['ocr'],'')
        self.assertEqual(reconcile_passes(a,'六肖：鼠牛虎兔龙马','2026284',True)['ocr'],'')
        spaced='六 肖 ： 鼠 牛 虎 兔 龙 蛇'
        self.assertTrue(reconcile_passes(spaced,a,'2026284',True)['ocr'])
        kill='杀二肖：猴马（上期杀牛龙）'
        self.assertEqual(reconcile_passes(kill,kill,'2026284',True)['accepted_groups'][0]['picks'],list('猴马'))
