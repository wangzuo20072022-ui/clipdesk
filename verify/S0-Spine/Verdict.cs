using System;

namespace S0Spine;

/// <summary>
/// 把每一步实测结果记录成「判定表」。
/// 跑完直接把这部分输出贴进 docs\技术验证报告.md —— 省掉"跑完忘了写下结论"这一步。
/// </summary>
internal sealed class Verdict
{
    public sealed record Row(string Id, string Question, string Answer, bool? Passed, string Evidence);

    private readonly System.Collections.Generic.List<Row> _rows = new();

    public void Log(string line)
    {
        Console.WriteLine(line);
    }

    public void Record(string id, string question, string answer, bool? passed, string evidence)
    {
        _rows.Add(new Row(id, question, answer, passed, evidence));
        string mark = passed switch { true => "✅", false => "❌", null => "⬜" };
        Console.WriteLine($"  {mark} [{id}] {answer}");
        if (!string.IsNullOrEmpty(evidence)) Console.WriteLine($"      证据：{evidence}");
    }

    public void PrintTable()
    {
        Console.WriteLine();
        Console.WriteLine("================ 判定表（直接贴进 docs\\技术验证报告.md）================");
        Console.WriteLine();
        Console.WriteLine("| # | 问题 | 判定 | 实测结果 | 证据 |");
        Console.WriteLine("|---|---|---|---|---|");
        foreach (var r in _rows)
        {
            string mark = r.Passed switch { true => "✅ 通过", false => "❌ 不通过", null => "⬜ 需人工确认" };
            Console.WriteLine($"| {r.Id} | {r.Question} | {mark} | {r.Answer} | {r.Evidence} |");
        }
        Console.WriteLine();
        Console.WriteLine("======================================================================");
    }
}
