﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.PowerFx.Core.IR;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.PowerFx.Core.Public.Values;
using Microsoft.PowerFx.Core.Functions;
using Microsoft.PowerFx.Core.Public.Types;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace Microsoft.PowerFx.Functions
{
    internal static partial class Library
    {
        // Char is used for PA string escaping 
        public static FormulaValue Char(IRContext irContext, NumberValue[] args)
        {
            var arg0 = args[0];
            var str = new string((char)arg0.Value, 1);
            return new StringValue(irContext, str);
        }

        public static FormulaValue Concat(EvalVisitor runner, SymbolContext symbolContext, IRContext irContext, FormulaValue[] args)
        {// Streaming 
            var arg0 = (TableValue)args[0];
            var arg1 = (LambdaFormulaValue)args[1];

            StringBuilder sb = new StringBuilder();

            foreach (var row in arg0.Rows)
            {
                if (row.IsValue)
                {
                    var childContext = symbolContext.WithScopeValues(row.Value);

                    // Filter evals to a boolean 
                    var result = arg1.Eval(runner, childContext);

                    var str = (StringValue)result;
                    sb.Append(str.Value);
                }
            }

            return new StringValue(irContext, sb.ToString());
        }

        // Scalar 
        // Operator & maps to this function call. 
        public static FormulaValue Concatenate(IRContext irContext, StringValue[] args)
        {
            StringBuilder sb = new StringBuilder();

            foreach (var arg in args)
            {
                sb.Append(arg.Value);
            }

            return new StringValue(irContext, sb.ToString());
        }

        // https://docs.microsoft.com/en-us/powerapps/maker/canvas-apps/functions/function-value
        // Convert string to number
        public static FormulaValue Value(EvalVisitor runner, SymbolContext symbolContext, IRContext irContext, FormulaValue[] args)
        {
            var arg0 = args[0];

            if (arg0 is NumberValue n)
            {
                return n;
            }

            if (arg0 is DateValue dv)
            {
                return DateToNumber(irContext, new DateValue[] { dv });
            }

            if (arg0 is DateTimeValue dtv)
            {
                return DateTimeToNumber(irContext, new DateTimeValue[] { dtv });
            }

            string str = null;
            if (arg0 is CustomObjectValue cov)
            {
                JsonElement element = cov.Element;
                switch (element.ValueKind)
                {
                    case JsonValueKind.String:
                        str = element.GetString();
                        break;
                    case JsonValueKind.Number:
                        return new NumberValue(irContext, element.GetDouble());
                    case JsonValueKind.Null:
                        return new BlankValue(irContext);
                }
            }

            if (arg0 is StringValue sv)
            {
                str = sv.Value;
            }

            if (string.IsNullOrEmpty(str))
            {
                return new BlankValue(irContext);
            }

            double div = 1;
            if (str[str.Length - 1] == '%')
            {
                str = str.Substring(0, str.Length - 1);
                div = 100;
            }

            if (!double.TryParse(str, NumberStyles.Any, runner.CultureInfo, out var val))
            {
                return CommonErrors.InvalidNumberFormatError(irContext);
            }

            if (IsInvalidDouble(val))
            {
                return CommonErrors.ArgumentOutOfRange(irContext);
            }

            val /= div;

            return new NumberValue(irContext, val);
        }

        // https://docs.microsoft.com/en-us/powerapps/maker/canvas-apps/functions/function-text
        public static FormulaValue Text(EvalVisitor runner, SymbolContext symbolContext, IRContext irContext, FormulaValue[] args)
        {
            if (args.Length > 1)
            {
                return CommonErrors.NotYetImplementedError(irContext, "Text() doesn't support format args");
            }

            // $$$ combine with a ToString()? 
            // $$$ Should these be handled by coercion?
            string str = null;
            var arg0 = args[0];
            if (arg0 is NumberValue num)
            {
                str = num.Value.ToString();
            }
            else if (arg0 is StringValue s)
            {
                str = s.Value;
            }
            else if (arg0 is DateValue d)
            {
                // $$$ Use real format string
                str = d.Value.ToString("M/d/yyyy", runner.CultureInfo);
            }
            else if (arg0 is DateTimeValue dt)
            {
                // $$$ Use real format string
                str = dt.Value.ToString("g", runner.CultureInfo);
            }
            else if (arg0 is TimeValue t)
            {
                var tDate = new DateTime().Add(t.Value);
                str = tDate.ToString("t", runner.CultureInfo);
            }

            if (str != null)
            {
                return new StringValue(irContext, str);
            }

            return CommonErrors.NotYetImplementedError(irContext, $"Text format for {arg0.GetType().Name}");
        }

        // https://docs.microsoft.com/en-us/powerapps/maker/canvas-apps/functions/function-isblank-isempty
        // Take first non-blank value. 
        // 
        public static FormulaValue Coalesce(EvalVisitor runner, SymbolContext symbolContext, IRContext irContext, FormulaValue[] args)
        {
            var errors = new List<ErrorValue>();

            foreach (var arg in args)
            {
                var res = runner.EvalArg<ValidFormulaValue>(arg, symbolContext, arg.IRContext);

                if (res.IsValue)
                {
                    var val = res.Value;
                    if (!(val is StringValue str && str.Value == ""))
                    {
                        if (errors.Count == 0)
                            return res.ToFormulaValue();
                        else
                            return ErrorValue.Combine(irContext, errors);
                    }
                }
                if (res.IsError)
                {
                    errors.Add(res.Error);
                }
            }
            if (errors.Count == 0)
                return new BlankValue(irContext);
            else
                return ErrorValue.Combine(irContext, errors);
        }

        public static FormulaValue Lower(IRContext irContext, StringValue[] args)
        {
            return new StringValue(irContext, args[0].Value.ToLower());
        }

        public static FormulaValue Upper(IRContext irContext, StringValue[] args)
        {
            return new StringValue(irContext, args[0].Value.ToUpper());
        }

        // https://docs.microsoft.com/en-us/powerapps/maker/canvas-apps/functions/function-len
        public static FormulaValue Len(IRContext irContext, StringValue[] args)
        {
            return new NumberValue(irContext, args[0].Value.Length);
        }

        // https://docs.microsoft.com/en-us/powerapps/maker/canvas-apps/functions/function-left-mid-right
        public static FormulaValue Mid(IRContext irContext, FormulaValue[] args)
        {
            var errors = new List<ErrorValue>();
            NumberValue start = (NumberValue)args[1];
            if (double.IsNaN(start.Value) || double.IsInfinity(start.Value) || start.Value <= 0)
            {
                errors.Add(CommonErrors.ArgumentOutOfRange(start.IRContext));
            }

            NumberValue count = (NumberValue)args[2];
            if (double.IsNaN(count.Value) || double.IsInfinity(count.Value) || count.Value < 0)
            {
                errors.Add(CommonErrors.ArgumentOutOfRange(count.IRContext));
            }

            if (errors.Count != 0)
            {
                return ErrorValue.Combine(irContext, errors);
            }

            StringValue source = (StringValue)args[0];
            var start0Based = (int)(start.Value - 1);
            if (source.Value == "" || start0Based >= source.Value.Length)
            {
                return new StringValue(irContext, "");
            }

            var minCount = Math.Min((int)count.Value, source.Value.Length - start0Based);
            var result = source.Value.Substring(start0Based, minCount);

            return new StringValue(irContext, result);
        }

        public static FormulaValue Left(IRContext irContext, FormulaValue[] args)
        {
            StringValue source = (StringValue)args[0];
            NumberValue count = (NumberValue)args[1];

            if (count.Value >= source.Value.Length)
            {
                return source;
            }

            return new StringValue(irContext, source.Value.Substring(0, (int)count.Value));
        }

        public static FormulaValue Right(IRContext irContext, FormulaValue[] args)
        {
            StringValue source = (StringValue)args[0];
            NumberValue count = (NumberValue)args[1];

            if(count.Value == 0)
            {
                return new StringValue(irContext, "");
            }

            if(count.Value >= source.Value.Length)
            {
                return source;
            }

            return new StringValue(irContext, source.Value.Substring(source.Value.Length - (int)count.Value, (int)count.Value));
        }

        private static FormulaValue Replace(IRContext irContext, FormulaValue[] args)
        {
            StringValue source = (StringValue)args[0];
            NumberValue start = (NumberValue)args[1];
            NumberValue count = (NumberValue)args[2];
            StringValue replacement = (StringValue)args[3];

            var start0Based = (int)(start.Value - 1);
            var prefix = start0Based < source.Value.Length ? source.Value.Substring(0, start0Based) : source.Value;

            var suffixIndex = start0Based + (int)count.Value;
            var suffix = suffixIndex < source.Value.Length ? source.Value.Substring(suffixIndex) : string.Empty;
            var result = prefix + replacement.Value + suffix;

            return new StringValue(irContext, result);
        }

        public static FormulaValue Split(EvalVisitor runner, SymbolContext symbolContext, IRContext irContext, StringValue[] args)
        {
            var text = args[0].Value;
            var separator = args[1].Value;

            // The separator can be zero, one, or more characters that are matched as a whole in the text string. Using a zero length or blank
            // string results in each character being broken out individually.
            var substrings = string.IsNullOrEmpty(separator) ? text.Select(c => new string(c, 1)) : text.Split(new string[] { separator }, StringSplitOptions.None);
            var rows = substrings.Select(s => new StringValue(IRContext.NotInSource(FormulaType.String), s));

            return new InMemoryTableValue(irContext, StandardSingleColumnTableFromValues(irContext, rows.ToArray(), BuiltinFunction.OneColumnTableResultName));
        }

        private static FormulaValue Substitute(IRContext irContext, FormulaValue[] args)
        {
            StringValue source = (StringValue)args[0];

            if (args[1] is BlankValue || (args[1] is StringValue sv && string.IsNullOrEmpty(sv.Value)))
            {
                return source;
            }

            StringValue match = (StringValue)args[1];
            StringValue replacement = (StringValue)args[2];

            int instanceNum = -1;
            if (args[3] is NumberValue nv)
            {
                instanceNum = (int)nv.Value;
            }

            var sourceValue = source.Value;
            var idx = sourceValue.IndexOf(match.Value);
            if (instanceNum < 0)
            {
                while (idx >= 0)
                {
                    var temp = sourceValue.Substring(0, idx) + replacement.Value;
                    sourceValue = sourceValue.Substring(idx + match.Value.Length);
                    var idx2 = sourceValue.IndexOf(match.Value);
                    if (idx2 < 0)
                    {
                        idx = idx2;
                    }
                    else
                    {
                        idx = temp.Length + idx2;
                    }
                    sourceValue = temp + sourceValue;
                }
            }
            else
            {
                var num = 0;
                while (idx >= 0 && ++num < instanceNum)
                {
                    var idx2 = sourceValue.Substring(idx + match.Value.Length).IndexOf(match.Value);
                    if (idx2 < 0)
                    {
                        idx = idx2;
                    }
                    else
                    {
                        idx += match.Value.Length + idx2;
                    }
                }

                if (idx >= 0 && num == instanceNum)
                {
                    sourceValue = sourceValue.Substring(0, idx) + replacement.Value + sourceValue.Substring(idx + match.Value.Length);
                }
            }
            return new StringValue(irContext, sourceValue);
        }

        public static FormulaValue StartsWith(IRContext irContext, StringValue[] args)
        {
            StringValue text = args[0];
            StringValue start = args[1];

            return new BooleanValue(irContext, text.Value.StartsWith(start.Value));
        }

        public static FormulaValue EndsWith(IRContext irContext, StringValue[] args)
        {
            StringValue text = args[0];
            StringValue end = args[1];

            return new BooleanValue(irContext, text.Value.EndsWith(end.Value));
        }

        public static FormulaValue Trim(IRContext irContext, StringValue[] args)
        {
            StringValue text = args[0];

            // Remove all whitespace except ASCII 10, 11, 12, 13 and 160, then trim to follow Excel's behavior
            var regex = new Regex(@"[^\S\xA0\n\v\f\r]+");

            var result = regex.Replace(text.Value, " ").Trim();

            return new StringValue(irContext, result);
        }

        public static FormulaValue TrimEnds(IRContext irContext, StringValue[] args)
        {
            StringValue text = args[0];

            var result = text.Value.Trim();

            return new StringValue(irContext, result);
        }
    }
}
