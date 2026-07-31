using System;
using System.Collections.Generic;
using System.Linq;

namespace 六合分析软件
{
    public class AnalysisEngine
    {

        // 统计号码出现次数
        public static Dictionary<string, int> CountNumbers(List<string> numbers)
        {
            Dictionary<string, int> result = new Dictionary<string, int>();

            foreach (string num in numbers)
            {
                if (result.ContainsKey(num))
                {
                    result[num]++;
                }
                else
                {
                    result[num] = 1;
                }
            }

            return result;
        }



        // 获取热号
        public static List<string> GetHotNumbers(List<string> numbers)
        {
            var data = CountNumbers(numbers);


            return data
                .OrderByDescending(x => x.Value)
                .Take(10)
                .Select(x => x.Key)
                .ToList();

        }



        // 获取冷号
        public static List<string> GetColdNumbers(List<string> numbers)
        {
            var data = CountNumbers(numbers);


            return data
                .OrderBy(x => x.Value)
                .Take(10)
                .Select(x => x.Key)
                .ToList();

        }



        // 简单走势分析
        public static string TrendAnalysis(List<string> numbers)
        {

            if (numbers == null || numbers.Count == 0)
            {
                return "暂无历史数据";
            }


            var hot = GetHotNumbers(numbers);


            string result = "近期热门号码：\r\n";


            foreach (var n in hot)
            {
                result += n + "  ";
            }


            return result;

        }

    }
}