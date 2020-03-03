// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Hl7.Fhir.Introspection;
using Hl7.Fhir.Model;
using Hl7.FhirPath.Expressions;
using Microsoft.Health.Fhir.Core.Models;
using EnumerableReturnType=System.Collections.Generic.IEnumerable<Microsoft.Health.Fhir.Core.UnitTests.Features.Search.SearchParameterTypeResult>;
using Expression = Hl7.FhirPath.Expressions.Expression;
using Range = Hl7.Fhir.Model.Range;
using SearchParamType = Microsoft.Health.Fhir.ValueSets.SearchParamType;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Search
{
    public static class SearchParameterToTypeResolver
    {
        private static readonly ModelInspector ModelInspector = new ModelInspector();

        internal static Action<string> Log { get; set; } = s => Debug.WriteLine(s);

        public static EnumerableReturnType Resolve(
            string resourceType,
            (SearchParamType type, Expression expression, Uri definition) typeAndExpression,
            (SearchParamType type, Expression expression, Uri definition)[] componentExpressions)
        {
            Type typeForFhirType = ModelInfoProvider.GetTypeForFhirType(resourceType);

            if (componentExpressions?.Any() == true)
            {
                foreach (var component in componentExpressions)
                {
                    var context = Context.WithParentType(typeForFhirType, component.type, component.definition, typeAndExpression.expression);

                    foreach (SearchParameterTypeResult classMapping in ClassMappings(context, component.expression))
                    {
                        yield return classMapping;
                    }
                }
            }
            else
            {
                var context = Context.WithParentType(typeForFhirType, typeAndExpression.type, typeAndExpression.definition);

                foreach (SearchParameterTypeResult classMapping in ClassMappings(context, typeAndExpression.expression))
                {
                    yield return classMapping;
                }
            }

            EnumerableReturnType ClassMappings(Context context, Expression expr)
            {
                foreach (var result in Accept(expr, context)
                    .GroupBy(x => x.ClassMapping.Name)
                    .Select(x => x.FirstOrDefault())
                    .ToArray())
                {
                    yield return result;
                }
            }
        }

        private static EnumerableReturnType Visit(ChildExpression expression, Context ctx)
        {
            if (expression.FunctionName == "builtin.children")
            {
                var newCtx = ctx;
                if (expression.ChildName != null)
                {
                    newCtx = ctx.WithPath(expression.ChildName);
                }

                foreach (var type in Accept(expression.Focus, newCtx))
                {
                    yield return type;
                }
            }
            else
            {
                throw new NotImplementedException();
            }
        }

        private static EnumerableReturnType Visit(BinaryExpression expression, Context ctx)
        {
            if (expression.Op == "as")
            {
                var constantExp = expression.Arguments.OfType<ConstantExpression>().Single().Value as string;
                var mapping = GetMapping(constantExp);

                ctx = ctx.WithAsType(mapping);

                foreach (var result in Accept(expression.Right, ctx.Clone()))
                {
                    yield return result;
                }
            }
            else if (expression.Op == "|" ||
                     expression.Op == "!=" ||
                     expression.Op == "==" ||
                     expression.Op == "and")
            {
                foreach (var innerExpression in expression.Arguments)
                {
                    foreach (var result in Accept(innerExpression, ctx.Clone()))
                    {
                        yield return result;
                    }
                }
            }
            else
            {
                throw new NotImplementedException();
            }
        }

        private static EnumerableReturnType Visit(FunctionCallExpression expression, Context ctx)
        {
            if (expression.Focus != null)
            {
                if (expression.FunctionName == "type")
                {
                    // Ignore
                    yield break;
                }
                else if (expression.FunctionName == "exists" || expression.FunctionName == "is")
                {
                    yield return new SearchParameterTypeResult(GetMapping(typeof(FhirBoolean)), ctx.SearchParamType, null, ctx.Definition);
                    yield break;
                }
                else if (expression.FunctionName == "as")
                {
                    // Matches Condition.abatement.as(Age)
                    var constantExp = expression.Arguments.OfType<ConstantExpression>().Single().Value as string;

                    var mapping = GetMapping(constantExp);
                    ctx = ctx.WithAsType(mapping).Clone();

                    foreach (var type in ClassMappings(ctx))
                    {
                        yield return type;
                    }

                    yield break;
                }
                else if (expression.FunctionName == "where" ||
                         expression.FunctionName == "builtin.children" ||
                         expression.FunctionName == "builtin.item")
                {
                    foreach (var type in ClassMappings(ctx))
                    {
                        yield return type;
                    }

                    yield break;
                }
            }

            throw new NotImplementedException();

            EnumerableReturnType ClassMappings(Context c)
            {
                return Accept(expression.Focus, c);
            }
        }

        private static EnumerableReturnType Visit(AxisExpression expression, Context ctx)
        {
            if (ctx.ParentExpression != null)
            {
                foreach (var result in Accept(ctx.ParentExpression, ctx.CloneAsChildExpression()))
                {
                    yield return result;
                }
            }
            else
            {
                var pathBuilder = new StringBuilder(ctx.Path.First().Item1);

                var skipResourceElement = true;
                var mapping = ctx.Path.First().Item2;

                if (mapping == null && ModelInfoProvider.Instance.GetTypeForFhirType(ctx.Path.First().Item1) != null)
                {
                    mapping = GetMapping(ctx.Path.First().Item1);
                }

                // Default to parent resource
                if (mapping == null)
                {
                    mapping = ctx.ParentTypeMapping;
                    skipResourceElement = false;
                }

                foreach (var item in ctx.Path.Skip(skipResourceElement ? 1 : 0))
                {
                    pathBuilder.AppendFormat(".{0}", item.Item1);
                    if (item.Item2 != null)
                    {
                        pathBuilder.AppendFormat("({0})", item.Item2.Name);
                        mapping = item.Item2;
                        continue;
                    }

                    var prop = mapping.PropertyMappings.FirstOrDefault(x => x.Name == item.Item1);
                    if (prop != null)
                    {
                        if (prop.GetElementType() == typeof(Element))
                        {
                            string path = pathBuilder.ToString();
                            foreach (var fhirType in prop.FhirType)
                            {
                                yield return new SearchParameterTypeResult(GetMapping(fhirType), ctx.SearchParamType, path, ctx.Definition);
                            }

                            pathBuilder.AppendFormat("({0})", string.Join(",", prop.FhirType.Select(x => x.Name)));
                            Log($"Resolved path '{pathBuilder}'");
                            yield break;
                        }

                        mapping = GetMapping(prop.GetElementType());
                    }
                    else
                    {
                        break;
                    }
                }

                Log($"Resolved path '{pathBuilder}'");
                yield return new SearchParameterTypeResult(mapping, ctx.SearchParamType, pathBuilder.ToString(), ctx.Definition);
            }
        }

        private static EnumerableReturnType Visit(ConstantExpression expression, Context ctx)
        {
            yield break;
        }

        private static EnumerableReturnType Visit(VariableRefExpression expression, Context ctx)
        {
            // matches %resource.referenceSeq.chromosome
            if (string.Equals(expression.Name, "resource", StringComparison.OrdinalIgnoreCase))
            {
                var newContext = ctx.WithPath("Resource", ctx.ParentTypeMapping);
                return Accept(new AxisExpression("that"), newContext);
            }

            throw new NotImplementedException();
        }

        private static EnumerableReturnType Visit(Expression expression, Context ctx)
        {
            throw new NotImplementedException();
        }

        private static EnumerableReturnType Accept(Expression expression, Context ctx)
        {
            return (EnumerableReturnType)Visit((dynamic)expression, ctx);
        }

        private static ClassMapping GetMapping(string type)
        {
            switch (type.ToUpperInvariant())
            {
                case "AGE":
                    return GetMapping(typeof(Age));
                case "DATETIME":
                case "DATE":
                    return GetMapping(typeof(FhirDateTime));
                case "URI":
                    return GetMapping(typeof(FhirUri));
                case "BOOLEAN":
                    return GetMapping(typeof(FhirBoolean));
                case "STRING":
                    return GetMapping(typeof(FhirString));
                case "PERIOD":
                    return GetMapping(typeof(Period));
                case "RANGE":
                    return GetMapping(typeof(Range));
                default:
                    return GetMapping(ModelInfoProvider.Instance.GetTypeForFhirType(type));
            }
        }

        private static ClassMapping GetMapping(Type type)
        {
            ClassMapping returnValue = ModelInspector.FindClassMappingByType(type);

            if (returnValue == null)
            {
                return ModelInspector.ImportType(type);
            }

            return returnValue;
        }

        private class Context
        {
            public Stack<(string, ClassMapping)> Path { get; set; } = new Stack<(string, ClassMapping)>();

            public ClassMapping ParentTypeMapping { get; set; }

            public Expression ParentExpression { get; set; }

            public ClassMapping AsTypeMapping { get; set; }

            public Uri Definition { get; set; }

            public Microsoft.Health.Fhir.ValueSets.SearchParamType SearchParamType { get; set; }

            public Context WithAsType(ClassMapping asTypeMapping)
            {
                Context ctx = Clone();
                ctx.AsTypeMapping = asTypeMapping;
                return ctx;
            }

            public Context WithPath(string propertyName, ClassMapping knownMapping = null)
            {
                Context ctx = Clone();
                ctx.Path.Push((propertyName, knownMapping ?? AsTypeMapping));
                return ctx;
            }

            public static Context WithParentType(
                Type type,
                SearchParamType paramType,
                Uri definition,
                Expression parentExpression = null)
            {
                var ctx = new Context
                {
                    ParentExpression = parentExpression,
                    ParentTypeMapping = ClassMapping.Create(type),
                    SearchParamType = paramType,
                    Definition = definition,
                };

                return ctx;
            }

            public Context Clone()
            {
                var clone = new Stack<(string, ClassMapping)>();
                foreach (var item in Path.Reverse())
                {
                    clone.Push(item);
                }

                var ctx = new Context
                {
                    Path = clone,
                    AsTypeMapping = AsTypeMapping,
                    ParentExpression = ParentExpression,
                    SearchParamType = SearchParamType,
                    ParentTypeMapping = ParentTypeMapping,
                    Definition = Definition,
                };

                return ctx;
            }

            public Context CloneAsChildExpression()
            {
                var ctx = Clone();
                ctx.ParentExpression = null;
                return ctx;
            }
        }
    }
}