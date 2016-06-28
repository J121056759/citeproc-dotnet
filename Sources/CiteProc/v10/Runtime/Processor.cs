﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;
using CiteProc.Compilation;
using CiteProc.Formatting;

namespace CiteProc.v10.Runtime
{
    /// <summary>
    /// Represents a processor for styles that adhere to the CSL v1.0.1 specification.
    /// See http://docs.citationstyles.org/en/stable/specification.html.
    /// </summary>
    public abstract partial class Processor : CiteProc.Processor
    {
        private LocaleProvider[] _LocaleProviders;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="id"></param>
        /// <param name="title"></param>
        protected Processor(string id, string title)
            : base(new Version(1, 0, 1), id, title)
        {
            // create instances of available locale providers
            this._LocaleProviders = this.GetLocaleProviders().ToArray();
        }

        protected ComposedRun[] GenerateBibliography(IDataProvider[] items, string locale, bool forceLocale, SortComparer comparer)
        {
            // init
            var localeProvider = this.GetLocaleProvider(locale, forceLocale);
            var p = new Parameters();

            // generate entries
            var entries = items
                .Select(item =>
                {
                    // init
                    var c = new ExecutionContext(item, localeProvider);

                    // render
                    return this.GenerateBibliographyEntry(c, p);
                })
                .ToArray();

            // done
            return entries
                .OrderBy(x => x.Sort, comparer)
                .Select(x => x.Layout)
                .ToArray();
        }
        protected abstract Entry GenerateBibliographyEntry(ExecutionContext c, Parameters p);

        protected ComposedRun GenerateCitation(IDataProvider[] items, string locale, bool forceLocale, string delimiter, SortComparer comparer)
        {
            // init
            var localeProvider = this.GetLocaleProvider(locale, forceLocale);
            var p = new Parameters();

            // generate cites
            var cites = items
                .Select(item =>
                {
                    // init
                    var context = new ExecutionContext(item, localeProvider);

                    // render
                    return this.GenerateCitationEntry(context, p);
                })
                .ToArray();

            // done
            switch (cites.Length)
            {
                case 0:
                    return null;
                case 1:
                    return cites.Single().Layout;
                default:
                    // init
                    var runs = cites
                        .OrderBy(x => x.Sort, comparer)
                        .Select(x => (Run)x.Layout)
                        .ToList();

                    // insert
                    this.ApplyDelimiter(runs, p.GenerateText(delimiter));

                    // done
                    return new ComposedRun(null, runs.ToArray(), false);
            }
        }
        protected abstract Entry GenerateCitationEntry(ExecutionContext c, Parameters p);

        protected virtual bool InitializeWithHyphen
        {
            get
            {
                return true;
            }
        }
        protected virtual PageRangeFormats PageRangeFormat
        {
            get
            {
                return PageRangeFormats.Expanded;
            }
        }
        protected virtual DemotingBehavior DemoteNonDroppingParticle
        {
            get
            {
                return DemotingBehavior.DisplayAndSort;
            }
        }

        protected abstract IEnumerable<LocaleProvider> GetLocaleProviders();
        protected virtual string DefaultLocale
        {
            get
            {
                return "en-US";
            }
        }
        private LocaleProvider GetLocaleProvider(string locale, bool force)
        {
            // init
            var culture = new Culture(force ? locale : this.DefaultLocale);

            // dialect?
            var result = this._LocaleProviders
                .SingleOrDefault(x => x.Culture == culture);

            // language?
            if (result == null)
            {
                result = this._LocaleProviders
                    .SingleOrDefault(x => x.Culture == culture.Language);
            }

            // invariant?
            if (result == null)
            {
                result = this._LocaleProviders
                    .Single(x => x.Culture == Culture.Invariant);
            }

            // done
            return result;
        }

        #region Compilation
        internal static new Compiler Compile(File[] files)
        {
            // find dependent and independent file
            var dependents = files
                .OfType<StyleFile>()
                .Where(x => x.IsDependent)
                .ToArray();
            var independents = files
                .OfType<StyleFile>()
                .Where(x => !dependents.Contains(x))
                .ToArray();

            // valid?
            if (independents.Length != 1)
            {
                throw new CompilerException("A single independent style (and no more) is required.");
            }
            else if (dependents.Length > 1)
            {
                throw new CompilerException("A single dependent style is allowed.");
            }

            // init
            var independent = independents.Single();
            var dependent = dependents.SingleOrDefault();

            // merge style
            var style = independent;
            if (dependent != null)
            {
                // check that dependent actually depends on given independent
                var link = (dependent.Info == null || dependent.Info.Links == null ? null : dependent.Info.Links.SingleOrDefault(x => x.Relation == LinkRelation.IndepdendentParent));
                if (link == null || independent.Info == null || link.Href != independent.Info.Id)
                {
                    throw new CompilerException(dependent, "The dependent and independent style files do not match.");
                }

                // clone
                style = style.Clone();

                // copy
                style.Info = dependent.Info;
            }

            // create compiler
            var code = new Compiler();

            // usings
            code.AppendUsing("System");
            code.AppendUsing("System.Collections.Generic");
            code.AppendUsing("System.Linq");
            code.AppendUsing("CiteProc.Formatting");

            // namespace
            using (var ns = code.AppendNamespace("CiteProc.v10.Runtime.Compiled"))
            {
                // class
                using (var cl = ns.AppendClass("public class CompiledProcessor : CiteProc.v10.Runtime.Processor"))
                {
                    // constructor
                    using (var block = cl.AppendMethod("public CompiledProcessor() : base({0}, {1})", Compiler.GetLiteral(style.Info.Id), Compiler.GetLiteral(style.Info.Title.Text)))
                    {
                    }

                    // compile style
                    style.Compile(cl);

                    // compile locale providers
                    var providers = LocaleProvider.Compile(cl, files.OfType<LocaleFile>().ToArray(), independent.Locales ?? new LocaleElement[] { }, (dependent != null && dependent.Locales != null ? dependent.Locales : new LocaleElement[] { }));

                    // locales
                    using (var block = cl.AppendMethod("protected override IEnumerable<LocaleProvider> GetLocaleProviders()"))
                    {
                        foreach (var provider in providers)
                        {
                            block.AppendLine("yield return new {0}();", provider);
                        }
                    }
                }
            }

            // done
            return code;
        }

#if(DEBUG)
        public static string GenerateDefaultLocaleProviders()
        {
            // init
            var code = new Compiler();
            var files = LocaleFile.Defaults;

            // usings
            code.AppendUsing("System");
            code.AppendUsing("System.Collections.Generic");
            code.AppendUsing("System.Linq");

            // namespace
            using (var ns = code.AppendNamespace("CiteProc.v10.Runtime"))
            {
                // partial class
                using (var cl = ns.AppendClass("partial class Processor"))
                {
                    // locale provider
                    using (var lp = cl.AppendNestedClass("partial class LocaleProvider"))
                    {
                        // compile
                        var providers = LocaleProvider.CompileLocaleFiles(lp, files, "public");
                    }
                }
            }

            // done
            return code.ToString();
        }

#endif

        #endregion

        #region Rendering
        protected Entry RenderStyle(ExecutionContext c, Parameters p, Func<Parameters, Entry> child)
        {
            return child(p);
        }
        protected Entry RenderBibliography(ExecutionContext c, Parameters p, Func<Parameters, ComposedRun> layout, Func<Parameters, string[]> sort)
        {
            return new Entry(layout(p), sort(p));
        }
        protected Entry RenderCitation(ExecutionContext c, Parameters p, Func<Parameters, ComposedRun> layout, Func<Parameters, string[]> sort)
        {
            return new Entry(layout(p), sort(p));
        }

        protected ComposedRun RenderLayout(string tag, string prefix, string suffix, ExecutionContext c, Parameters p, Func<Parameters, Result[]> children)
        {
            // init
            var results = children(p)
                .Select(x => x.ToComposedRun(c, p))
                .ToArray();

            // done
            return new Result(tag, results, false, prefix, suffix, false, null)
                .ToComposedRun(c, p);
        }
        protected Result RenderMacro(string tag, ExecutionContext c, Parameters p, Func<Parameters, Result[]> children)
        {
            // init
            var results = children(p)
                .Select(x => x.ToComposedRun(c, p))
                .ToArray();

            // done
            return new Result(tag, results, false, null, null, false, null);
        }
        protected Result RenderGroup(string tag, string delimiter, string prefix, string suffix, ExecutionContext c, Parameters p, Func<Parameters, Result[]> children)
        {
            // init
            var results = children(p)
                .Select(x => x.ToComposedRun(c, p))
                .OfType<Run>()
                .ToList();

            // cs:group implicitly acts as a conditional: cs:group and its child elements are suppressed if 
            // a) at least one rendering element in cs:group calls a variable (either directly or via a macro), and 
            // b) all variables that are called are empty.
            // flatten
            var byVariable = results
                .Cast<ComposedRun>()
                .SelectMany(x => x.GetComposedRuns())
                .Where(x => x.ByVariable)
                .ToArray();
            if (byVariable.Length > 0 && byVariable.All(x => x.IsEmpty))
            {
                // suppress
                results.Clear();
            }

            // delimiter
            this.ApplyDelimiter(results, p.GenerateText(delimiter));

            // done
            return new Result(tag, results, (byVariable.Length > 0), prefix, suffix, false, null);
        }
        protected Result RenderChoose(string tag, ExecutionContext c, Parameters p, Func<Parameters, IEnumerable<Result>> parts)
        {
            // init
            var results = parts(p)
                .Where(x => x != null)
                .Select(x => x.ToComposedRun(c, p))
                .Where(x => x != null)
                .ToArray();

            // done
            return new Result(tag, results, false, null, null, false, null);
        }

        protected Result RenderLabel(string tag, string variable, TermName term, TermFormat format, LabelPluralization plurilization, string prefix, string suffix, ExecutionContext c, Parameters p)
        {
            // get variable
            var value = c.GetVariableAsNumber(variable) as INumberVariable;

            // find text
            string text = null;
            if (value != null)
            {
                // plural?
                var plural = false;
                switch (plurilization)
                {
                    case LabelPluralization.Always:
                        plural = true;
                        break;
                    case LabelPluralization.Contextual:
                        plural = (value.Min != value.Max);
                        break;
                    case LabelPluralization.Never:
                        plural = false;
                        break;
                }

                // get localized term
                text = c.Locale.GetTerm(term, format, plural);
            }

            // done
            return new Result(tag, p.GenerateText(text), true, prefix, suffix, false, null);
        }
        protected Result RenderNumber(string tag, string variable, TermName? term, NumberFormat format, string prefix, string suffix, TextCase? textCase, ExecutionContext c, Parameters p)
        {
            // init
            var value = c.GetVariableAsNumber(variable);

            // to text
            string text = null;

            // result?
            if (value == null)
            {
                text = null;
            }
            else if (value is string)
            {
                text = (string)value;
            }
            else if (value is INumberVariable)
            {
                text = this.RenderNumber((INumberVariable)value, term, format, c, p);
            }
            else
            {
                throw new NotSupportedException();
            }

            // done
            return this.RenderTextByValue(tag, text, prefix, suffix, false, textCase, c, p);
        }
        private string RenderNumber(INumberVariable value, TermName? term, NumberFormat format, ExecutionContext c, Parameters p)
        {
            // result?
            if (value == null)
            {
                return null;
            }
            else
            {
                // init
                var text = string.Empty;

                // number
                var number = (INumberVariable)value;

                // gender
                var gender = (term.HasValue ? c.Locale.GetTermGender(term.Value) : (Gender?)null);

                // range?
                if (number.Min == number.Max)
                {
                    // single number
                    text = c.Locale.FormatNumber(number.Max, format, gender);
                }
                else
                {
                    // range
                    if (number.Separator == '-' && term.HasValue && term.Value == TermName.Page)
                    {
                        // page range
                        var delimiter = c.Locale.GetTerm(TermName.PageRangeDelimiter, TermFormat.Long, true);

                        // done
                        text = this.RenderPageRange(number.Min, number.Max, delimiter);
                    }
                    else
                    {
                        // normal range with hyphen
                        text = string.Format("{0}{1}{2}{3}{4}",
                            c.Locale.FormatNumber(number.Min, format, gender),
                            (number.Separator == '&' ? " " : ""),
                            number.Separator,
                            (number.Separator == '&' || number.Separator == ',' ? " " : ""),
                            c.Locale.FormatNumber(number.Max, format, gender));
                    }
                }

                // done
                return text;
            }
        }
        private string RenderPageRange(uint min, uint max, string delimiter)
        {
            // init
            var format = this.PageRangeFormat;

            // max > min?
            if (min > max)
            {
                format = PageRangeFormats.Expanded;
            }

            // init
            var from = min.ToString().Reverse().ToArray();
            var to = max.ToString().Reverse().ToArray();

            // find delta
            var delta = to
                .Select((c, i) => (i >= from.Length || from[i] != c ? i + 1 : 0))
                .Max();

            // chicago
            if (format == PageRangeFormats.Chicago)
            {
                if (min < 100)
                {
                    // Less than 100
                    format = PageRangeFormats.Expanded;
                }
                else if (min >= 1000 & (to.Length - delta) <= 1)
                {
                    // 4 digits
                    format = PageRangeFormats.Expanded;
                }
                else if ((min % 100) == 0)
                {
                    // 100 or multiple of 100
                    format = PageRangeFormats.Expanded;
                }
                else if ((min % 100) < 10)
                {
                    // 101 through 109 (in multiples of 100)
                    format = PageRangeFormats.Minimal;
                }
                else
                {
                    // 110 through 199 (in multiples of 100)
                    format = PageRangeFormats.MinimalTwo;
                }
            }

            // adjust delta
            switch (format)
            {
                case PageRangeFormats.Expanded:
                    delta = to.Length;
                    break;
                case PageRangeFormats.Minimal:
                    // no action
                    break;
                case PageRangeFormats.MinimalTwo:
                    delta = Math.Max(delta, 2);
                    break;
                default:
                    throw new NotSupportedException();
            }

            // done
            return string.Format("{0}{1}{2}", min, delimiter, new string(to.Take(delta).Reverse().ToArray()));
        }

        protected Result RenderTextByValue(string tag, string value, string prefix, string suffix, bool quotes, TextCase? textCase, ExecutionContext context, Parameters p)
        {
            // init
            return new Result(tag, p.GenerateText(value), false, prefix, suffix, quotes, textCase);
        }
        protected Result RenderTextByVariable(string tag, string variable, TermName? term, TermFormat format, string prefix, string suffix, bool quotes, TextCase? textCase, ExecutionContext c, Parameters p)
        {
            // init
            object value = null;
            if (format == TermFormat.Short)
            {
                value = c.GetVariable(string.Format("{0}-short", variable));
            }
            if (value == null)
            {
                value = c.GetVariable(variable);
            }

            // number?
            string text = null;
            if (value == null)
            {
                text = null;
            }
            else if (value is INumberVariable)
            {
                // number
                text = this.RenderNumber((INumberVariable)value, term, NumberFormat.Numeric, c, p);
            }
            else
            {
                // cast
                text = (value is string ? (string)value : value.ToString());
            }

            // done
            return new Result(tag, p.GenerateText(text), true, prefix, suffix, quotes, textCase);
        }
        protected Result RenderTextByMacro(string tag, Func<ExecutionContext, Parameters, Result> macro, string prefix, string suffix, bool quotes, TextCase? textCase, ExecutionContext c, Parameters p)
        {
            // init
            var result = macro.Invoke(c, p).ToComposedRun(c, p);

            // render
            return new Result(tag, new Run[] { result }, false, prefix, suffix, quotes, textCase);
        }
        protected Result RenderTextByTerm(string tag, TermName term, TermFormat format, bool plural, string prefix, string suffix, bool quotes, TextCase? textCase, ExecutionContext c, Parameters parameters)
        {
            // get text from term
            var text = c.Locale.GetTerm(term, format, plural);

            // done
            return this.RenderTextByValue(tag, text, prefix, suffix, quotes, textCase, c, parameters);
        }

        protected Result RenderNonLocalizedDate(string tag, string variable, string delimiter, string prefix, string suffix, ExecutionContext c, Parameters p, Func<Parameters, DatePartParameters[]> dateParts)
        {
            // done
            return this.RenderDate(tag, variable, delimiter, prefix, suffix, c, p, dateParts(p));
        }
        protected Result RenderLocalizedDate(string tag, string variable, DateFormat format, DatePrecision precision, string prefix, string suffix, ExecutionContext c, Parameters p, Func<Parameters, DatePartParameters[]> dateParts)
        {
            // init
            var locales = c.Locale.GetDateParts(format, p);
            var scopes = dateParts(p);

            // merge date part definitions
            var parts = locales
                .Select(locale =>
                {
                    // find match
                    var match = scopes.SingleOrDefault(x => x.Name == locale.Name);

                    // done (and keep suffix/prefix from locale date part parameters)
                    if (match == null)
                    {
                        return locale;
                    }
                    else
                    {
                        return new DatePartParameters(
                            match.Tag,
                            match,
                            match.Name,
                            match.Format ?? locale.Format,
                            locale.Prefix,
                            locale.Suffix,
                            match.TextCase ?? locale.TextCase
                        );
                    }
                })
                .Where(part =>
                {
                    // filter for precision
                    switch (part.Name)
                    {
                        case DatePartName.Day:
                            return (precision == DatePrecision.YearMonthDay);
                        case DatePartName.Month:
                            return (precision == DatePrecision.YearMonthDay || precision == DatePrecision.YearMonth);
                        case DatePartName.Year:
                            return true;
                        default:
                            throw new NotSupportedException();
                    }
                })
                .ToArray();

            // done
            return this.RenderDate(tag, variable, null, prefix, suffix, c, p, parts);
        }
        private Result RenderDate(string tag, string variable, string delimiter, string prefix, string suffix, ExecutionContext c, Parameters p, DatePartParameters[] dateParts)
        {
            // get date
            var value = c.GetVariableAsDate(variable);

            // format
            Run[] results = null;
            if (value == null)
            {
                results = new Run[] { };
            }
            else if (value is string)
            {
                // render as text
                results = p.GenerateText((string)value).ToArray();
            }
            else
            {
                // cast
                var date = (IDateVariable)value;

                // find available date parts
                var available = new List<DatePartName>()
                {
                    DatePartName.Year
                };
                if (date.MonthFrom.HasValue && date.MonthTo.HasValue && dateParts.Any(x => x.Name == DatePartName.Month))
                {
                    // add month
                    available.Add(DatePartName.Month);

                    // day?
                    if (date.DayFrom.HasValue && date.DayTo.HasValue && dateParts.Any(x => x.Name == DatePartName.Day))
                    {
                        available.Add(DatePartName.Day);
                    }
                }

                // find differing date parts
                DatePartName[] differing = null;
                if (date.YearFrom != date.YearTo)
                {
                    // year, month, day
                    differing = available.ToArray();
                }
                else if (date.MonthFrom.HasValue && date.MonthTo.HasValue && date.MonthFrom.Value != date.MonthTo.Value)
                {
                    // month, day
                    differing = available
                        .Where(x => x != DatePartName.Year)
                        .ToArray();
                }
                else if (date.DayFrom.HasValue && date.DayTo.HasValue && date.DayFrom.Value != date.DayTo.Value)
                {
                    // day
                    differing = available
                        .Where(x => x == DatePartName.Day)
                        .ToArray();
                }
                else
                {
                    // none
                    differing = new DatePartName[] { };
                }

                // range?
                if (differing.Length == 0)
                {
                    // render single date
                    results = this.RenderDateParts(date.YearFrom, date.SeasonFrom, date.MonthFrom, date.DayFrom, c, p, dateParts, delimiter, true, true)
                        .ToArray();
                }
                else
                {
                    // render from
                    var delimiterIndex = Enumerable.Range(1, dateParts.Length)
                        .Select(i => dateParts.Take(i).Select(x => x.Name).ToArray())
                        .First(a => differing.All(x => a.Contains(x)))
                        .Length;
                    var fromParts = dateParts
                        .Take(delimiterIndex)
                        .ToArray();
                    var from = this.RenderDateParts(date.YearFrom, date.SeasonFrom, date.MonthFrom, date.DayFrom, c, p, fromParts, delimiter, true, false);

                    // render to
                    var toParts = dateParts
                        .Where(x => differing.Contains(x.Name) || !fromParts.Contains(x))
                        .ToArray();
                    var to = this.RenderDateParts(date.YearTo, date.SeasonTo, date.MonthTo, date.DayTo, c, p, toParts, delimiter, false, true);

                    // render
                    results = from
                        .Concat(p.GenerateText("–"))
                        .Concat(to)
                        .ToArray();
                }
            }

            // done
            return new Result(tag, results, true, prefix, suffix, false, null);
        }
        private IEnumerable<Run> RenderDateParts(int year, Season? season, int? month, int? day, ExecutionContext c, Parameters p, DatePartParameters[] parts, string delimiter, bool renderFirstPrefix, bool renderLastSuffix)
        {
            // init
            var results = parts
                .Select((part, i) =>
                {
                    // init
                    var renderPrefix = (i > 0 || renderFirstPrefix);
                    var renderSuffix = (i < parts.Length - 1 || renderLastSuffix);

                    // done
                    return this.RenderDatePart(year, season, month, day, c, part, renderPrefix, renderSuffix)
                        .ToComposedRun(c, p);
                })
                .OfType<Run>()
                .ToList();

            // delimiter
            this.ApplyDelimiter(results, p.GenerateText(delimiter));

            // done
            return results;
        }
        private Result RenderDatePart(int year, Season? season, int? month, int? day, ExecutionContext c, DatePartParameters p, bool renderPrefix, bool renderSuffix)
        {
            // init
            string text = null;

            // per name
            switch (p.Name)
            {
                case DatePartName.Year:
                    // year
                    if (year != 0)
                    {
                        switch (p.Format)
                        {
                            case DatePartFormat.Long:
                                text = string.Format("{0}{1}", Math.Abs(year), (year < 0 ? c.Locale.GetTerm(TermName.Bc, TermFormat.Long, false) : (year < 1000 ? c.Locale.GetTerm(TermName.Ad, TermFormat.Long, false) : null)));
                                break;
                            case DatePartFormat.Short:
                                text = (year % 100).ToString("00");
                                break;
                            default:
                                throw new NotSupportedException();
                        }
                    }
                    break;
                case DatePartName.Month:
                    if (month.HasValue && month.Value >= 1 && month.Value <= 12)
                    {
                        // month
                        switch (p.Format)
                        {
                            case DatePartFormat.Numeric:
                                text = month.Value.ToString();
                                break;
                            case DatePartFormat.NumericLeadingZeros:
                                text = month.Value.ToString("00");
                                break;
                            case DatePartFormat.Long:
                                text = c.Locale.GetTerm(this.GetTermName(month.Value), TermFormat.Long, false);
                                break;
                            case DatePartFormat.Short:
                                text = c.Locale.GetTerm(this.GetTermName(month.Value), TermFormat.Short, false);
                                break;
                            default:
                                throw new NotSupportedException();
                        }
                    }
                    else if (season.HasValue)
                    {
                        // season
                        switch (season.Value)
                        {
                            case Season.Spring:
                                text = c.Locale.GetTerm(TermName.Season01, TermFormat.Long, false);
                                break;
                            case Season.Summer:
                                text = c.Locale.GetTerm(TermName.Season02, TermFormat.Long, false);
                                break;
                            case Season.Autumn:
                                text = c.Locale.GetTerm(TermName.Season03, TermFormat.Long, false);
                                break;
                            case Season.Winter:
                                text = c.Locale.GetTerm(TermName.Season04, TermFormat.Long, false);
                                break;
                            default:
                                throw new NotSupportedException();
                        }
                    }
                    break;
                case DatePartName.Day:
                    // day
                    if (day.HasValue && day.Value >= 1 && day.Value <= 31)
                    {
                        switch (p.Format)
                        {
                            case DatePartFormat.Numeric:
                                text = day.Value.ToString();
                                break;
                            case DatePartFormat.NumericLeadingZeros:
                                text = day.Value.ToString("00");
                                break;
                            case DatePartFormat.Ordinal:
                                if (c.Locale.LimitDayOrdinalsToDay1 && day.Value > 1)
                                {
                                    text = day.Value.ToString();
                                }
                                else
                                {
                                    // get gender
                                    var gender = (month.HasValue ? c.Locale.GetTermGender(this.GetTermName(month.Value)) : (Gender?)null);

                                    // done
                                    text = c.Locale.FormatNumberAsOrdinal((uint)day.Value, gender);
                                }
                                break;
                            default:
                                throw new NotSupportedException();
                        }
                    }
                    break;
            }

            // done
            return this.RenderTextByValue(p.Tag, text, (renderPrefix ? p.Prefix : null), (renderSuffix ? p.Suffix : null), false, p.TextCase, c, p);
        }
        private TermName GetTermName(int month)
        {
            switch (month)
            {
                case 1:
                    return TermName.Month01;
                case 2:
                    return TermName.Month02;
                case 3:
                    return TermName.Month03;
                case 4:
                    return TermName.Month04;
                case 5:
                    return TermName.Month05;
                case 6:
                    return TermName.Month06;
                case 7:
                    return TermName.Month07;
                case 8:
                    return TermName.Month08;
                case 9:
                    return TermName.Month09;
                case 10:
                    return TermName.Month10;
                case 11:
                    return TermName.Month11;
                case 12:
                    return TermName.Month12;
                default:
                    throw new NotSupportedException();
            }
        }

        protected Result RenderNames(string id, string[] variables, TermName?[] terms, string prefix, string suffix, ExecutionContext c, Parameters p, Func<Parameters, NameParameters> nameParameters, Func<Parameters, EtAlParameters> etAlParameters, Func<Parameters, LabelParameters> labelParameters)
        {
            // init
            var np = nameParameters(p);
            var eap = etAlParameters(p);
            var lp = labelParameters(p);

            // create groups
            var groups = variables
                .Select((v, i) => new NameGroup(v, terms[i], c.GetVariableAsNames(v)))
                .Where(x => x.Names != null)
                .ToList();

            // merge editor and translator
            var editors = groups.SingleOrDefault(x => x.Term.HasValue && x.Term.Value == TermName.Editor);
            var translators = groups.SingleOrDefault(x => x.Term.HasValue && x.Term.Value == TermName.Translator);
            if (editors != null && translators != null)
            {
                // identical?
                if (editors.Names.Select(x => x.ToString()).SequenceEqual(translators.Names.Select(x => x.ToString())))
                {
                    // insert
                    groups.Insert(groups.IndexOf(editors), new NameGroup(null, TermName.EditorTranslator, editors.Names));
                    groups.Remove(editors);
                    groups.Remove(translators);
                }
            }

            // create results
            List<Run> results = null;
            if (np.NameFormat == NameFormat.Count)
            {
                // count
                var count = groups
                    .Select(x => (x.Names.Length >= np.EtAlMin ? np.EtAlUseFirst : x.Names.Length))
                    .Sum();

                // done
                results = new List<Run>(p.GenerateText(count > 0 ? count.ToString() : ""));
            }
            else
            {
                // render
                results = groups
                    .Select(x => this.RenderNameGroup(x, c, p, np, eap, lp))
                    .Select(x => x.ToComposedRun(c, p))
                    .Cast<Run>()
                    .ToList();

                // delimiter
                this.ApplyDelimiter(results, p.GenerateText(p.NamesDelimiter));
            }

            // done
            return new Result(id, results, true, prefix, suffix, false, null);
        }
        private Result RenderNameGroup(NameGroup group, ExecutionContext c, Parameters p, NameParameters np, EtAlParameters eap, LabelParameters lp)
        {
            // init
            var etAlActive = (group.Names.Length >= np.EtAlMin);
            var count = (etAlActive ? np.EtAlUseFirst + 1 : group.Names.Length);
            var delta = (etAlActive ? 1 : 0);

            // render names
            var parts = group.Names
                .Where((x, i) => i < count - delta || i == group.Names.Length - 1)
                .Select((name, index) =>
                {
                    // invert name?
                    var inverted = false;
                    switch (np.NameAsSortOrder)
                    {
                        case NameSortOptions.None:
                            inverted = false;
                            break;
                        case NameSortOptions.First:
                            inverted = (index == 0);
                            break;
                        case NameSortOptions.All:
                            inverted = true;
                            break;
                    }

                    // render name
                    return new
                    {
                        Name = this.RenderName(name, inverted, c, np).ToComposedRun(c, p),
                        Inverted = inverted,
                        IsSecondToLast = (index == count - 2),
                        IsLast = (index == count - 1)
                    };
                })
                .ToArray();

            // create results
            var results = new List<Run>();
            foreach (var part in parts)
            {
                // name
                if (part.IsLast && etAlActive && !np.EtAlUseLast)
                {
                    // add 'et-al' only if any names are rendered (which is not the case when et-al-use-first="0")
                    if (results.Count > 0)
                    {
                        // et al
                        var result = new Result(eap.Tag, eap.GenerateText(c.Locale.GetTerm(eap.Term, TermFormat.Long, false)), false, null, null, false, null);

                        // add
                        results.Add(result.ToComposedRun(c, p));
                    }
                }
                else
                {
                    // name
                    results.Add(part.Name);
                }

                // delimiter
                if (part.IsLast)
                {
                    // no delimiter
                }
                else if (part.IsSecondToLast)
                {
                    // init
                    var addDelimiter = true;

                    // et al or and?
                    if (etAlActive)
                    {
                        // et al
                        addDelimiter = this.EvaluateDelimiterBehavior(np.DelimiterPrecedesEtAl, (count > 2), part.Inverted);
                    }
                    else if (np.And != And.Delimiter)
                    {
                        // and
                        addDelimiter = this.EvaluateDelimiterBehavior(np.DelimiterPrecedesLast, (count >= 3), part.Inverted);
                    }

                    // add delimiter
                    results.AddRange(p.GenerateText(addDelimiter ? np.NameDelimiter : " "));

                    // and
                    if (!etAlActive)
                    {
                        switch (np.And)
                        {
                            case And.Symbol:
                                results.AddRange(np.GenerateText("&"));
                                results.AddRange(np.GenerateText(" "));
                                break;
                            case And.Text:
                                results.AddRange(np.GenerateText(c.Locale.GetTerm(TermName.And, TermFormat.Long, false)));
                                results.AddRange(np.GenerateText(" "));
                                break;
                        }
                    }

                    // et-al-use-last
                    if (etAlActive && np.EtAlUseLast)
                    {
                        // ellipsis
                        results.AddRange(np.GenerateText("… "));
                    }
                }
                else
                {
                    // add delimiter
                    results.AddRange(p.GenerateText(np.NameDelimiter));
                }
            }

            // label
            if (lp != null && group.Term.HasValue)
            {
                // plural?
                var plural = false;
                switch (lp.Plural)
                {
                    case LabelPluralization.Always:
                        plural = true;
                        break;
                    case LabelPluralization.Contextual:
                        plural = (group.Names.Length != 1);
                        break;
                    case LabelPluralization.Never:
                        plural = false;
                        break;
                    default:
                        throw new NotSupportedException();
                }

                // render
                var text = lp.GenerateText(c.Locale.GetTerm(group.Term.Value, lp.Format, plural));
                var label = new Result(lp.Tag, text, false, lp.Prefix, lp.Suffix, false, lp.TextCase);

                // add
                results.Add(label.ToComposedRun(c, p));
            }

            // done
            return new Result(np.Tag, results, false, null, null, false, null);
        }
        private Result RenderName(object name, bool inverted, ExecutionContext c, NameParameters p)
        {
            // init
            IEnumerable<Run> results = null;

            if (name is string)
            {
                results = p.GenerateText((string)name);
            }
            else if (name is INameVariable)
            {
                results = this.RenderName((INameVariable)name, inverted, c, p);
            }
            else
            {
                throw new NotSupportedException();
            }

            // done
            return new Result(p.Tag, results, false, p.Prefix, p.Suffix, false, null);
        }
        private IEnumerable<Run> RenderName(INameVariable name, bool inverted, ExecutionContext c, NameParameters p)
        {
            // init
            var familyParameters = p.NameParts[0];
            var givenParameters = p.NameParts[1];
            var suffixParameters = new NamePartParameters(null, p, null, null, null);

            // format name parts
            var familyName = this.FormatNamePart(c, familyParameters, name.FamilyName);
            var nonDroppingParticles = this.FormatNamePart(c, familyParameters, name.NonDroppingParticles);
            var given = this.FormatNamePart(c, givenParameters, this.InitializeGivenNames(name, p.Initialize, p.InitializeWith));
            var droppingParticles = this.FormatNamePart(c, givenParameters, name.DroppingParticles);
            var suffix = this.FormatNamePart(c, suffixParameters, name.PrecedeSuffixByComma && !string.IsNullOrWhiteSpace(name.Suffix) ? string.Format(",{0}", name.Suffix) : name.Suffix);

            // space delimiter
            var space = p.GenerateText(" ");

            // render parts
            Result[] results = null;
            string delimiter = null;
            switch (p.NameFormat)
            {
                case NameFormat.Long:
                    // inverted?
                    if (inverted)
                    {
                        // demote non dropping particle?
                        if (this.DemoteNonDroppingParticle == DemotingBehavior.DisplayAndSort)
                        {
                            // yes
                            delimiter = p.SortSeparator;
                            results = new Result[]
                            {
                                this.RenderNamePart(familyParameters, space, familyName),
                                this.RenderNamePart(givenParameters, space, given, droppingParticles, nonDroppingParticles),
                                this.RenderNamePart(suffixParameters, space, suffix)
                            };
                        }
                        else
                        {
                            // no
                            delimiter = p.SortSeparator;
                            results = new Result[]
                            {
                                this.RenderNamePart(familyParameters, space, nonDroppingParticles, familyName),
                                this.RenderNamePart(givenParameters, space, given, droppingParticles),
                                this.RenderNamePart(suffixParameters, space, suffix)
                            };
                        }
                    }
                    else
                    {
                        // not inverted
                        delimiter = " ";
                        results = new Result[]
                        {
                            this.RenderNamePart(givenParameters, space, given),
                            this.RenderNamePart(familyParameters, space, droppingParticles, nonDroppingParticles, familyName, suffix)
                        };
                    }
                    break;
                case NameFormat.Short:
                    // short
                    delimiter = null;
                    results = new Result[]
                    {
                        this.RenderNamePart(familyParameters, space, nonDroppingParticles, familyName),
                    };
                    break;
                default:
                    throw new NotSupportedException();
            }

            // create runs
            var runs = results
                .Select(x => x.ToComposedRun(c, p))
                .Cast<Run>()
                .ToList();

            // apply delimiters
            this.ApplyDelimiter(runs, p.GenerateText(delimiter));

            // done
            return runs;
        }
        private TextRun[] FormatNamePart(ExecutionContext c, NamePartParameters p, string text)
        {
            // render
            return new Result(null, p.GenerateText(text), false, null, null, false, p.TextCase)
                .ToComposedRun(c, p)
                .Children
                .Cast<TextRun>()
                .Where(x => !x.IsEmpty)
                .ToArray();
        }
        private Result RenderNamePart(NamePartParameters p, IEnumerable<TextRun> space, params TextRun[][] parts)
        {
            // filter
            var filtered = parts
                .Where(part => part.Length > 0)
                .ToArray();

            // join
            var results = filtered
                .SelectMany((part, i) =>
                {
                    // last?
#warning To APOSTROPHE constant
                    if (i == filtered.Length - 1 || "'’‘".Contains(part.Last().Text.Last()))
                    {
                        // no space
                        return part;
                    }
                    else
                    {
                        // with space
                        return part.Concat(space);
                    }
                })
                .ToArray();

            // done
            return new Result(p.Tag, results, false, p.Prefix, p.Suffix, false, null);
        }
        private string InitializeGivenNames(INameVariable name, bool initialize, string initializeWith)
        {
            // processing required?
            if (initializeWith == null || string.IsNullOrEmpty(name.GivenNames) || string.IsNullOrWhiteSpace(name.FamilyName))
            {
                // nope
                return name.GivenNames;
            }

            // split
            var names = name.GivenNames
                .Split(new char[] { ' ', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(givenName =>
                {
                    // initial?
                    if (givenName.Length == 1)
                    {
                        // initial
                        return string.Format("{0}{1}", givenName.ToUpper(), initializeWith);
                    }
                    else
                    {
                        // initialize?
                        if (initialize)
                        {
                            // compound name?
                            var parts = givenName.Split(new char[] { '-', '_', '–' }, StringSplitOptions.RemoveEmptyEntries);

                            // done
                            if (this.InitializeWithHyphen)
                            {
                                return string.Format("{0}{1}", string.Join(string.Format("{0}-", initializeWith.Trim()), parts.Select(x => x.Substring(0, 1).ToUpper())), initializeWith);
                            }
                            else
                            {
                                return string.Join("", parts.Select(x => string.Format("{0}{1}", x.Substring(0, 1).ToUpper(), initializeWith)));
                            }
                        }
                        else
                        {
                            // return full name
                            return string.Format("{0} ", givenName);
                        }
                    }
                })
                .ToArray();

            // done
            return string.Join("", names).Trim();
        }
        private bool EvaluateDelimiterBehavior(DelimiterBehavior behavior, bool isContext, bool isInverted)
        {
            switch (behavior)
            {
                case DelimiterBehavior.AfterInvertedName:
                    return isInverted;
                case DelimiterBehavior.Always:
                    return true;
                case DelimiterBehavior.Contextual:
                    return isContext;
                case DelimiterBehavior.Never:
                    return false;
                default:
                    throw new NotSupportedException();
            }
        }

        protected string[] RenderSort(string id, ExecutionContext c, Parameters p, Func<Parameters, string[]> keys)
        {
            return keys(p);
        }
        protected string RenderKeyByVariable(string id, string variable, ExecutionContext c, Parameters p)
        {
            // init
            var value = c.GetVariable(variable);

            // create key
            if (value == null || value is string)
            {
                return (string)value;
            }
            else if (value is IDateVariable)
            {
                // cast
                var date = (IDateVariable)value;

                // done
                return string.Format("{0:0000}{1:00}{2:00}-{3:0000}{4:00}{5:00}", date.YearFrom, date.MonthFrom ?? 0, date.DayFrom ?? 0, date.YearTo, date.MonthTo ?? 0, date.DayTo ?? 0);
            }
            else if (value is INumberVariable)
            {
                throw new NotImplementedException();
            }
            else if (value is object[])
            {
                // cast
                var names = (object[])value;

                // done
                return string.Join(",", names.Select(n =>
                {
                    // string or INameVariable?
                    if (n is INameVariable)
                    {
                        // cast
                        var name = (INameVariable)n;

                        // done
                        return string.Join(" ", new string[] { name.FamilyName, name.GivenNames, name.DroppingParticles, name.NonDroppingParticles, name.Suffix }.Where(x => !string.IsNullOrEmpty(x)));
                    }
                    else
                    {
                        return (string)n;
                    }
                }));
            }
            else
            {
                throw new NotSupportedException();
            }
        }
        protected string RenderKeyByMacro(string id, Func<ExecutionContext, Parameters, Result> macro, ExecutionContext c, Parameters p)
        {
            // init
            var result = macro.Invoke(c, p)
                .Children
                .Select(x => x.ToPlainText())
                .ToArray();

            // done
            return string.Join("", result);
        }

        private void ApplyDelimiter(List<Run> results, IEnumerable<Run> delimiter)
        {
            // init
            var parts = delimiter.ToArray();
            if (parts.Length > 0)
            {
                // init
                var index = 0;
                var isFirst = true;
                while (index < results.Count)
                {
                    // empty?
                    if (!results[index].IsEmpty)
                    {
                        // first?
                        if (!isFirst)
                        {
                            // insert
                            results.InsertRange(index, parts);

                            // add index
                            index += parts.Length;
                        }

                        // mark as not first
                        isFirst = false;
                    }

                    // next
                    index++;
                }
            }
        }

        #endregion
    }
}