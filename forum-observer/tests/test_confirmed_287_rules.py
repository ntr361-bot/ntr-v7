import unittest
from forum_observer.page_collector import score_posts
from forum_observer.core import vote_zodiacs
from forum_observer.evidence_audit import admit_one_material_per_author


def post(a,body,title="287期资料"):
    return dict(author=a,issue="2026287",title=title,body=body,images=[],
                detail_url="https://example.org/287")


class ConfirmedPlayTests(unittest.TestCase):
    def test_kill_more_than_six_rejected(self):
        self.assertEqual(len(vote_zodiacs(list("牛蛇狗龙羊猴"),"exclude","🔪杀肖")),6)
        with self.assertRaisesRegex(ValueError,"more than six"):
            vote_zodiacs(list("牛蛇狗龙羊猴鸡"),"exclude","🔪杀肖")

    def test_emoji_five_and_seven(self):
        text="287期：🔪 牛 蛇 狗 龙 羊 （固定统杀）\n287期：🔪 牛 蛇 狗 龙 羊 猴 鸡（个人自用）"
        _,rows=score_posts([post("刘爸爸",text)])
        selected=[r for r in rows if r["counted_for_ranking"]]
        self.assertEqual(len(selected),1,rows)
        self.assertEqual(selected[0]["picks"],list("牛蛇狗龙羊"))

    def test_single_double_combo(self):
        body="287期:必中单双【双肖+狗猴】开:？00准\n单肖：龙虎鼠狗猴马 双肖：兔牛猪鸡羊蛇"
        ranking,rows=score_posts([post("粗枝大叶",body,"港澳百家网【必中特码单双】")])
        selected=[x for x in rows if x["counted_for_ranking"]]
        self.assertEqual(len(selected),1,rows)
        self.assertEqual(selected[0]["picks"],list("兔牛猪鸡羊蛇狗猴"))
        self.assertAlmostEqual(sum(r["score"] for r in ranking),1,places=5)

    def test_bold_group_bracket(self):
        body="285期胆大胆小【胆小】开鼠19中\n286期胆大胆小【胆大】开羊24错\n287期胆大胆小【胆大】开？？中\n胆大：牛虎马猴狗猪\n胆小：鼠兔龙蛇羊鸡"
        _,rows=score_posts([post("呆头呆脑的猪",body,"胆大胆小-真金不怕火炼")])
        selected=[x for x in rows if x["counted_for_ranking"]]
        self.assertEqual(len(selected),1,rows)
        self.assertEqual(selected[0]["picks"],list("牛虎马猴狗猪"))

    def test_nine_over_all_other_plays(self):
        rows=[dict(author="甲",issue="2026287",mode="include",play="九肖",
                   counted_for_ranking=True,vote_zodiacs=list("鼠牛虎兔龙蛇马羊猴")),
              dict(author="甲",issue="2026287",mode="exclude",play="杀一肖",
                   counted_for_ranking=True,vote_zodiacs=list("鼠牛虎兔龙蛇马羊猴鸡狗")),
              dict(author="甲",issue="2026287",mode="include",play="八肖",
                   counted_for_ranking=True,vote_zodiacs=list("鼠牛虎兔龙蛇马羊"))]
        admit_one_material_per_author(rows)
        self.assertEqual([r["play"] for r in rows if r["counted_for_ranking"]],["九肖"])

    def test_plain_bracket_six_and_not_miezhuang(self):
        _,rows=score_posts([post("加油您最胖","【猪鸡马虎鼠龙】","六肖中特")])
        self.assertEqual(sum(r["counted_for_ranking"] for r in rows),1,rows)
        ranks,rows=score_posts([post("优优码皇","287期灭庄【马牛羊虎猴兔鸡龙蛇】开","杀三肖：")])
        self.assertEqual(ranks,[])


if __name__=="__main__":unittest.main()
