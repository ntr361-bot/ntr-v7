import unittest
from unittest.mock import patch
from forum_observer.comment_images import eligible_comment_image
from forum_observer.page_collector import score_posts,consistent_comment_ocr
from forum_observer.comment_collector import collect_comments


class CommentImagesTests(unittest.TestCase):
    def test_comments_keep_actual_author_and_exclude_after_draw_and_admin(self):
        rows=[]
        for author,stamp in [('甲','2026-10-11 20:00'),('乙','2026-10-11 20:01'),
                             ('论坛管理','2026-10-11 20:02'),('丙','2026-10-11 22:00')]:
            rows.append(dict(author=author,published_at=stamp,floor=author,text='六肖：鼠牛虎兔龙蛇',
                             image_urls=[],key=(author,stamp)))
        parent=dict(author='楼主',title='楼主的杀码帖',issue='2026284',evidence_id='parent',
                    draw_at='2026-10-11T21:32:00+08:00')
        with patch('forum_observer.comment_collector.incremental_scan',return_value=(rows,dict(truncated=False))):
            class Page:url='https://example.org/post'
            posts,audit=collect_comments(Page(),parent,0,{})
        self.assertEqual([p['author'] for p in posts],['甲','乙'])
        self.assertEqual(len(audit['rejected_rows']),2)
        self.assertTrue(all(p['parent_author']=='楼主' for p in posts))
        _, parsed=score_posts(posts)
        self.assertEqual({p['author'] for p in parsed if p['counted_for_ranking']},{'甲','乙'})

    def test_ocr_plays_must_agree_and_short_tips_cannot_pass(self):
        first='284期六肖：鼠牛虎兔龙蛇'
        self.assertEqual(consistent_comment_ocr(first,'284期 六肖【鼠牛虎兔龙蛇】','2026284'),first)
        self.assertEqual(consistent_comment_ocr(first,'284期六肖：鼠牛虎兔龙马','2026284'),'')
        self.assertEqual(consistent_comment_ocr('284期三肖：鼠牛虎','284期三肖：鼠牛虎','2026284'),'')
        self.assertEqual(consistent_comment_ocr(first,'283期六肖：鼠牛虎兔龙蛇','2026284'),'')

    def setUp(self):
        self.pic=dict(source_section='author_comment',author='甲',author_verified=True,
                      issue='2026284',src='https://example.org/tk118files/comment/image.jpg',
                      ocr='284期六肖：鼠牛虎兔龙蛇',ocr_status='recognized_consistent',
                      published_at='2026-10-11T20:00:00+08:00')
        self.post=dict(author='甲',issue='2026284',title='284期作品',body='',
                       images=[self.pic],detail_url='https://example.org/post')

    def test_verified_author_comment_image_can_vote(self):
        self.assertTrue(eligible_comment_image(self.pic,self.post))
        ranks,rows=score_posts([self.post])
        self.assertEqual(len(ranks),12)
        self.assertEqual(sum(p['counted_for_ranking'] for p in rows),1)

    def test_other_author_unknown_time_uncertain_ocr_and_after_draw_refused(self):
        for changes in (dict(author='乙'),dict(author_verified=False),dict(published_at='未知'),
                        dict(published_at='2026-10-11T22:00:00+08:00'),
                        dict(ocr_status='recognized'),dict(issue='2026283')):
            image=dict(self.pic,**changes)
            self.assertFalse(eligible_comment_image(image,self.post))
            self.assertEqual(score_posts([dict(self.post,images=[image])])[0],[])
