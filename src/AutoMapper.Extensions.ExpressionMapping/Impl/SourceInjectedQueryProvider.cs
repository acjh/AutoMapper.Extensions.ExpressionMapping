using AutoMapper.Internal;
using AutoMapper.Mappers;
using AutoMapper.QueryableExtensions.Impl;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AutoMapper.Extensions.ExpressionMapping.Impl
{
    using static Expression;
    using MemberPaths = IEnumerable<IEnumerable<MemberInfo>>;
    using ParameterBag = IDictionary<string, object>;

    public class SourceInjectedQueryProvider<TSource, TDestination> : IQueryProvider
    {
        private readonly IMapper _mapper;
        private readonly IQueryable<TSource> _dataSource;
        private readonly IQueryable<TDestination> _destQuery;
        private readonly IEnumerable<ExpressionVisitor> _beforeVisitors;
        private readonly IEnumerable<ExpressionVisitor> _afterVisitors;
        private readonly IDictionary<string, object> _parameters;
        private readonly MemberPaths _membersToExpand;
        private readonly Action<Exception> _exceptionHandler;

        public SourceInjectedQueryProvider(IMapper mapper,
            IQueryable<TSource> dataSource, IQueryable<TDestination> destQuery,
                IEnumerable<ExpressionVisitor> beforeVisitors,
                IEnumerable<ExpressionVisitor> afterVisitors,
                Action<Exception> exceptionHandler,
                IDictionary<string, object> parameters,
                MemberPaths membersToExpand)
        {
            _mapper = mapper;
            _dataSource = dataSource;
            _destQuery = destQuery;
            _beforeVisitors = beforeVisitors;
            _afterVisitors = afterVisitors;
            _parameters = parameters ?? new Dictionary<string, object>();
            _membersToExpand = membersToExpand ?? Enumerable.Empty<IEnumerable<MemberInfo>>();
            _exceptionHandler = exceptionHandler ?? (x => { });
        }

        public SourceInjectedQueryInspector Inspector { get; set; }
        internal Action<IEnumerable<object>> EnumerationHandler { get; set; }

        public IQueryable CreateQuery(Expression expression) 
            => new SourceSourceInjectedQuery<TSource, TDestination>(this, expression, EnumerationHandler, _exceptionHandler);

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) 
            => new SourceSourceInjectedQuery<TSource, TElement>(this, expression, EnumerationHandler, _exceptionHandler);

        public object Execute(Expression expression)
        {
            try
            {
                Inspector.StartQueryExecuteInterceptor(null, expression);

                var sourceExpression = ConvertDestinationExpressionToSourceExpression(expression);
                var sourceResult = InvokeSourceQuery(null, sourceExpression);

                Inspector.SourceResult(sourceExpression, sourceResult);
                return sourceResult;
            }
            catch (Exception x)
            {
                _exceptionHandler(x);
                throw;
            }
        }

        public TResult Execute<TResult>(Expression expression)
        {
            try
            {
                var resultType = typeof(TResult);
                Inspector.StartQueryExecuteInterceptor(resultType, expression);

                var sourceExpression = ConvertDestinationExpressionToSourceExpression(expression);

                var destResultType = typeof(TResult);
                var sourceResultType = CreateSourceResultType(destResultType);

                object destResult = null;

                // case #1: query is a projection from complex Source to complex Destination
                // example: users.UseAsDataSource().For<UserDto>().Where(x => x.Age > 20).ToList()
                if (IsProjection<TDestination>(resultType))
                {
                    // in case of a projection, we need an IQueryable
                    var sourceResult = _dataSource.Provider.CreateQuery(sourceExpression);
                    Inspector.SourceResult(sourceExpression, sourceResult);
                    
                    var queryExpressions = _mapper.ConfigurationProvider.Internal().ProjectionBuilder.GetProjection
                    (
                        sourceResult.ElementType, 
                        typeof(TDestination),
                        _parameters,
                        _membersToExpand.Select
                        (
                            m => new MemberPath
                            (
                                m.Distinct().ToArray()
                            )
                        ).ToArray()
                    );

                    destResult = (IQueryable<TDestination>)GetMapExpressions(queryExpressions).Aggregate(sourceResult, Select);
                }
                // case #2: query is arbitrary ("manual") projection
                // exaple: users.UseAsDataSource().For<UserDto>().Select(user => user.Age).ToList()
                // in case an arbitrary select-statement is enumerated, we do not need to map the expression at all
                // and cann safely return it
                else if (IsProjection(resultType, sourceExpression))
                {
                    var sourceResult = _dataSource.Provider.CreateQuery(sourceExpression);
                    var enumerator = sourceResult.GetEnumerator();
                    var elementType = ElementTypeHelper.GetElementType(typeof(TResult));
                    var constructorInfo = typeof(List<>).MakeGenericType(elementType).GetDeclaredConstructor(new Type[0]);
                    if (constructorInfo != null)
                    {
                        var listInstance = (IList)constructorInfo.Invoke(null);
                        while (enumerator.MoveNext())
                        {
                            listInstance.Add(enumerator.Current);
                        }
                        destResult = listInstance;
                    }
                }
                // case #2: projection to simple type
                // example: users.UseAsDataSource().For<UserDto>().FirstOrDefault(user => user.Age > 20)
                else
                {
                    /* 
                        in case of an element result (so instead of IQueryable<TResult>, just TResult)
                        we still want to support parameters.
                        This is e.g. the case, when the developer writes "UseAsDataSource().For<TResult>().FirstOrDefault(x => ...)
                        To still be able to support parameters, we need to create a query from it. 
                        That said, we need to replace the "element" operator "FirstOrDefault" with a "Where" operator, then apply our "Select" 
                        to map from TSource to TResult and finally re-apply the "element" operator ("FirstOrDefault" in our case) so only
                        one element is returned by our "Execute<TResult>" method. Otherwise we'd get an InvalidCastException

                        * So first we visit the sourceExpression and replace "element operators" with "where"
                        * then we create our mapping expression from TSource to TDestination (select) and apply it
                        * finally, we search for the element expression overload of our replaced "element operator" that has no expression as parameter
                            this expression is not needed anymore as it has already been applied to the "Where" operation and can be safely omitted
                        * when we're done creating our new expression, we call the underlying provider to execute it
                    */

                    // as we need to replace the innermost element of the expression,
                    // we need to traverse it first in order to find the node to replace or potential caveats
                    // e.g. .UseAsDataSource().For<Dto>().Select(e => e.Name).First()
                    //      in the above case we cannot map anymore as the "select" operator overrides our mapping.
                    var searcher = new ReplaceableMethodNodeFinder<TSource>();
                    searcher.Visit(sourceExpression);
                    // provide the replacer with our found MethodNode or <null>
                    var replacer = new MethodNodeReplacer<TSource>(searcher.MethodNode);

                    // default back to simple mapping of object instance for backwards compatibility (e.g. mapping non-nullable to nullable fields)
                    sourceExpression = replacer.Visit(sourceExpression);

                    if (replacer.FoundElementOperator)
                    {
                        /*  in case of primitive element operators (e.g. Any(), Sum()...)
                            we need to map the originating types (e.g. Entity to Dto) in this query
                            as the final value will be projected automatically
                            
                            == example 1 ==
                            UseAsDataSource().For<Dto>().Any(entity => entity.Name == "thename")
                            ..in that case sourceResultType and destResultType would both be "Boolean" which is not mappable for AutoMapper

                            == example 2 ==
                            UseAsDataSource().For<Dto>().FirstOrDefault(entity => entity.Name == "thename")
                            ..in this case the sourceResultType would be Entity and the destResultType Dto, so we can map the query directly
                        */

                        if (sourceResultType == destResultType)// && destResultType.IsPrimitive)
                        {
                            sourceResultType = typeof(TSource);
                            destResultType = typeof(TDestination);
                        }

                        var queryExpressions = _mapper.ConfigurationProvider.Internal().ProjectionBuilder.GetProjection
                        (
                            sourceResultType, 
                            destResultType, 
                            _parameters,
                            _membersToExpand.Select
                            (
                                m => new MemberPath
                                (
                                    m.Distinct().ToArray()
                                )
                            ).ToArray()
                        );
                        // add projection via "select" operator
                        var expr = GetMapExpressions(queryExpressions).Aggregate(sourceExpression, (source, lambda) => Select(source, lambda));
                        // in case an element operator without predicate expression was found (and thus not replaced)
                        var replacementMethod = replacer.ElementOperator;
                        // in case an element operator with predicate expression was replaced
                        if (replacer.ReplacedMethod != null)
                        {
                            // find replacement method that has no more predicates
                            replacementMethod = typeof(Queryable).GetAllMethods()
                                .Single(m => m.Name == replacer.ReplacedMethod.Name
#if NET45
                                            && m.GetParameters().All(p => typeof(Queryable).IsAssignableFrom(p.Member.ReflectedType))
#endif
                                            && m.GetParameters().Length == replacer.ReplacedMethod.GetParameters().Length - 1);
                        }

                        // reintroduce element operator that was replaced with a "where" operator to make it queryable
                        expr = Call(null,
                            replacementMethod.MakeGenericMethod(destResultType), expr);

                        destResult = _dataSource.Provider.Execute(expr);
                    }
                    // If there was no element operator that needed to be replaced by "where", just map the result
                    else
                    {
                        var sourceResult = _dataSource.Provider.Execute(sourceExpression);
                        Inspector.SourceResult(sourceExpression, sourceResult);
                        destResult = _mapper.Map(sourceResult, sourceResultType, destResultType);
                    }
                }

                Inspector.DestResult(destResult);

                // implicitly convert types in case of valuetypes which cannot be casted explicitly
                if (typeof(TResult).IsValueType() && destResult?.GetType() != typeof(TResult))
                    return (TResult)System.Convert.ChangeType(destResult, typeof(TResult));

                // if it is not a valuetype, we can safely cast it
                return (TResult)destResult;
            }
            catch (Exception x)
            {
                _exceptionHandler(x);
                throw;
            }
        }

        private LambdaExpression[] GetMapExpressions(QueryExpressions queryExpressions)
        {
            if (queryExpressions.LetClause != null && queryExpressions.Projection != null)
                return new LambdaExpression[] { queryExpressions.LetClause, queryExpressions.Projection };
            else if (queryExpressions.LetClause != null)
                return new LambdaExpression[] { queryExpressions.LetClause };
            else if (queryExpressions.Projection != null)
                return new LambdaExpression[] { queryExpressions.Projection };

            return new LambdaExpression[] { };
        }

        private static Expression Select(Expression source, LambdaExpression lambda)
        {
            return Call(
                    null,
                    QueryableSelectMethod.MakeGenericMethod(lambda.Parameters[0].Type, lambda.ReturnType),
                    new[] { source, Quote(lambda) }
                );
        }

        private static IQueryable Select(IQueryable source, LambdaExpression lambda) => source.Provider.CreateQuery(
                Call(
                    null,
                    QueryableSelectMethod.MakeGenericMethod(source.ElementType, lambda.ReturnType),
                    new[] { source.Expression, Expression.Quote(lambda) }
                    )
                );

        private object InvokeSourceQuery(Type sourceResultType, Expression sourceExpression)
        {
            var result = IsProjection<TSource>(sourceResultType)
                ? _dataSource.Provider.CreateQuery(sourceExpression)
                : _dataSource.Provider.Execute(sourceExpression);
            return result;
        }

        private static bool IsProjection<T>(Type resultType) => IsProjection(resultType) && resultType.GetGenericElementType() == typeof(T);

        private static bool IsProjection(Type resultType, Expression sourceExpression)
        {
            if (!IsProjection(resultType))
            {
                return false;
            }

            // in cases, where the query selects an IEnumerable itself, the former "IsProjection" condition is not sufficient
            // as it would detect that enuerable as projection
            // e.g. Source and Destination class both have a property "string[] Items{get;set;}"
            //      and the query was
            //      var result = sources.UseAsDataSource().For<Destination>().Select(dest => dest.Items).First();
            //      "result" would still be an IEnumerable and IsProjection would return "true"
            //      therefore we need to search for the existence of an "linq element operator" (an operator that returns a single element from an enumerable)
            var searcher = new ElementOperatorSearcher();
            searcher.Visit(sourceExpression);
            return !searcher.ContainsElementOperator;
        }

        private static bool IsProjection(Type resultType) 
            => resultType.IsEnumerableType() && !resultType.IsQueryableType() && resultType != typeof(string);

        private static Type CreateSourceResultType(Type destResultType)
        {
            var sourceResultType = destResultType.ReplaceItemType(typeof(TDestination), typeof(TSource));
            return sourceResultType;
        }

        private Expression ConvertDestinationExpressionToSourceExpression(Expression expression)
        {
            // call beforevisitors
            expression = _beforeVisitors.Aggregate(expression, (current, before) => before.Visit(current));

            var typeMap = _mapper.ConfigurationProvider.Internal().ResolveTypeMap(typeof(TDestination), typeof(TSource));
            var visitor = new ExpressionMapper.MappingVisitor(_mapper.ConfigurationProvider, typeMap, _destQuery.Expression, _dataSource.Expression, null,
                new[] { typeof(TSource) });
            var sourceExpression = visitor.Visit(expression);

            // apply parameters
            if (_parameters != null && _parameters.Any())
            {
                var constantVisitor = new ConstantExpressionReplacementVisitor(_parameters);
                sourceExpression = constantVisitor.Visit(sourceExpression);
            }

            // apply null guards in case the feature is enabled
            if (_mapper.ConfigurationProvider.Internal().EnableNullPropagationForQueryMapping)
            {
                var nullGuardVisitor = new NullsafeQueryRewriter();
                sourceExpression = nullGuardVisitor.Visit(sourceExpression);
            }
            // call aftervisitors
            sourceExpression = _afterVisitors.Aggregate(sourceExpression, (current, after) => after.Visit(current));

            return sourceExpression;
        }

        private static readonly MethodInfo QueryableSelectMethod = FindQueryableSelectMethod();

        private static MethodInfo FindQueryableSelectMethod()
        {
            Expression<Func<IQueryable<object>>> select = () => default(IQueryable<object>).Select(default(Expression<Func<object, object>>));
            var method = ((MethodCallExpression)select.Body).Method.GetGenericMethodDefinition();
            return method;
        }
    }

    internal class ConstantExpressionReplacementVisitor : ExpressionVisitor
    {
        private readonly ParameterBag _paramValues;

        public ConstantExpressionReplacementVisitor(
            ParameterBag paramValues) => _paramValues = paramValues;

        protected override Expression VisitMember(MemberExpression node)
        {
            if (!node.Member.DeclaringType.Has<CompilerGeneratedAttribute>())
            {
                return base.VisitMember(node);
            }
            var parameterName = node.Member.Name;
            if (!_paramValues.TryGetValue(parameterName, out object parameterValue))
            {
                const string vbPrefix = "$VB$Local_";
                if (!parameterName.StartsWith(vbPrefix, StringComparison.Ordinal) || !_paramValues.TryGetValue(parameterName.Substring(vbPrefix.Length), out parameterValue))
                {
                    return base.VisitMember(node);
                }
            }
            return Convert(Constant(parameterValue), node.Member.GetMemberType());
        }
    }

    internal class ReplaceableMethodNodeFinder<TDestination> : ExpressionVisitor
    {
        public MethodCallExpression MethodNode { get; private set; }
        private bool _ignoredMethodFound;
        private static readonly string[] IgnoredMethods = { "Select" };

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (_ignoredMethodFound)
            {
                return base.VisitMethodCall(node);
            }

            var isReplacableMethod = node.Method.DeclaringType == typeof(Queryable)
                                      && !IgnoredMethods.Contains(node.Method.Name)
                                      && !typeof(IQueryable).IsAssignableFrom(node.Method.ReturnType);

            // invalid method found => skip all (e.g. Select(entity=> (object)entity.Child1)
            if (isReplacableMethod &&
                !node.Method.ReturnType.IsPrimitive() && node.Method.ReturnType != typeof(TDestination))
            {
                return base.VisitMethodCall(node);
            }

            if (isReplacableMethod)
            {
                MethodNode = node;
            }
            // in case we find an incompatible method (Select), the already found MethodNode becomes invalid
            else if (IgnoredMethods.Contains(node.Method.Name))
            {
                _ignoredMethodFound = true;
                MethodNode = null;
            }

            return base.VisitMethodCall(node);
        }
    }

    internal class MethodNodeReplacer<TDestination> : ExpressionVisitor
    {
        private readonly MethodCallExpression _foundExpression;
        private static readonly MethodInfo QueryableWhereMethod = FindQueryableWhereMethod();


        private static MethodInfo FindQueryableWhereMethod()
        {
            Expression<Func<IQueryable<object>>> select = () => default(IQueryable<object>).Where(default(Expression<Func<object, bool>>));
            var method = ((MethodCallExpression)select.Body).Method.GetGenericMethodDefinition();
            return method;
        }

        public MethodNodeReplacer(MethodCallExpression foundExpression) => _foundExpression = foundExpression;

        public MethodInfo ReplacedMethod { get; private set; }

        public MethodInfo ElementOperator { get; private set; }

        public bool FoundElementOperator { get; private set; }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            // only replace first occurrence
            if (ReplacedMethod != null || _foundExpression == null)
            {
                return base.VisitMethodCall(node);
            }

            if (node == _foundExpression)
            {
                // if method has invalid type
                var parameters = node.Method.GetParameters();

                if (parameters.Length > 1 &&
                    typeof(Expression).IsAssignableFrom(parameters[1].ParameterType))
                {
                    FoundElementOperator = true;
                    ReplacedMethod = node.Method.GetGenericMethodDefinition();
                    return Call(null, QueryableWhereMethod.MakeGenericMethod(typeof(TDestination)),
                        node.Arguments);
                }
                // no predicate
                if (parameters.Length == 1)
                {
                    FoundElementOperator = true;
                    ElementOperator = node.Method.GetGenericMethodDefinition();
                    return node.Arguments[0];
                }
            }

            return base.VisitMethodCall(node);
        }
    }

    internal class ElementOperatorSearcher : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var isElementOperator = node.Method.DeclaringType == typeof(Queryable)
                                      && !typeof(IQueryable).IsAssignableFrom(node.Method.ReturnType);

            if (!ContainsElementOperator)
            {
                ContainsElementOperator = isElementOperator;
            }

            return base.VisitMethodCall(node);
        }

        public bool ContainsElementOperator { get; private set; }
    }
}