using Lucene.Net.Index;
using Lucene.Net.Search;
using SearchEngine;
using Query = Lucene.Net.Search.Query;
using TQ = TantivyDotNet.Query;

namespace ThreeWay;

public static class Queries
{
    static Query LT(string t) => new TermQuery(new Term("name", t));
    static BooleanQuery LB(params (Query q, Occur o)[] c) { var b = new BooleanQuery(); foreach (var (q, o) in c) b.Add(q, o); return b; }
    const string F = "name";

    public static readonly QuerySpec[] All =
    [
        new("miss", "term, no hit: zzzqqq",
            ("zzzqqq", WordMatchMethod.Exact, false), () => LT("zzzqqq"), () => TQ.Term(F, "zzzqqq")),
        new("term_rare", "term: final",
            ("final", WordMatchMethod.Exact, false), () => LT("final"), () => TQ.Term(F, "final")),
        new("term_common", "term: jpg (very common)",
            ("jpg", WordMatchMethod.Exact, false), () => LT("jpg"), () => TQ.Term(F, "jpg")),
        new("and", "report AND pdf",
            ("report AND pdf", WordMatchMethod.Exact, true),
            () => LB((LT("report"), Occur.MUST), (LT("pdf"), Occur.MUST)),
            () => TQ.Term(F, "report") & TQ.Term(F, "pdf")),
        new("or", "jpg OR png",
            ("jpg OR png", WordMatchMethod.Exact, true),
            () => LB((LT("jpg"), Occur.SHOULD), (LT("png"), Occur.SHOULD)),
            () => TQ.Term(F, "jpg") | TQ.Term(F, "png")),
        new("or_not", "(jpg OR png) AND NOT img",
            ("(jpg OR png) AND NOT img", WordMatchMethod.Exact, true),
            () => LB((LB((LT("jpg"), Occur.SHOULD), (LT("png"), Occur.SHOULD)), Occur.MUST), (LT("img"), Occur.MUST_NOT)),
            () => (TQ.Term(F, "jpg") | TQ.Term(F, "png")) & !TQ.Term(F, "img")),
        new("prefix", "prefix: rep*",
            ("rep*", WordMatchMethod.Exact, false), () => new PrefixQuery(new Term(F, "rep")), () => TQ.Regex(F, "rep.*")),
        new("suffix", "suffix: *ing",
            ("*ing", WordMatchMethod.Exact, false), () => new WildcardQuery(new Term(F, "*ing")), () => TQ.Regex(F, ".*ing")),
        new("infix", "infix: *port*",
            ("*port*", WordMatchMethod.Exact, false), () => new WildcardQuery(new Term(F, "*port*")), () => TQ.Regex(F, ".*port.*")),
        new("qmark", "pattern: dsc?????",
            ("dsc?????", WordMatchMethod.Exact, false), () => new WildcardQuery(new Term(F, "dsc?????")), () => TQ.Regex(F, "dsc....."))
        ,
        new("phrase", "phrase: \"report final\" (Sharp: not supported)",
            null,
            () => { var p = new PhraseQuery(); p.Add(new Term(F, "report")); p.Add(new Term(F, "final")); return p; },
            () => TQ.Phrase(F, "report final", 0)),
    ];
}
