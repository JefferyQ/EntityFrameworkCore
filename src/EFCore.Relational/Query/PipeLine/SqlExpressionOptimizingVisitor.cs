// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Relational.Query.Pipeline;
using Microsoft.EntityFrameworkCore.Relational.Query.Pipeline.SqlExpressions;

namespace Microsoft.EntityFrameworkCore.Query.Pipeline
{
    public class SqlExpressionOptimizingVisitor : ExpressionVisitor
    {
        private readonly ISqlExpressionFactory _sqlExpressionFactory;

        private readonly Dictionary<ExpressionType, ExpressionType> _expressionTypesNegationMap
            = new Dictionary<ExpressionType, ExpressionType>
            {
                { ExpressionType.Equal, ExpressionType.NotEqual },
                { ExpressionType.NotEqual, ExpressionType.Equal },
                { ExpressionType.GreaterThan, ExpressionType.LessThanOrEqual },
                { ExpressionType.GreaterThanOrEqual, ExpressionType.LessThan },
                { ExpressionType.LessThan, ExpressionType.GreaterThanOrEqual },
                { ExpressionType.LessThanOrEqual, ExpressionType.GreaterThan },
            };

        public SqlExpressionOptimizingVisitor(ISqlExpressionFactory sqlExpressionFactory)
        {
            _sqlExpressionFactory = sqlExpressionFactory;
        }

        protected override Expression VisitExtension(Expression extensionExpression)
        {
            if (extensionExpression is SqlUnaryExpression outerUnary)
            {
                // !(true) -> false
                // !(false) -> true
                if (outerUnary.OperatorType == ExpressionType.Not
                    && outerUnary.Operand is SqlConstantExpression innerConstantBool
                    && innerConstantBool.Type == typeof(bool))
                {
                    return (bool)innerConstantBool.Value
                        ? _sqlExpressionFactory.Constant(false, outerUnary.TypeMapping)
                        : _sqlExpressionFactory.Constant(true, outerUnary.TypeMapping);
                }

                // NULL IS NULL -> true
                if (outerUnary.OperatorType == ExpressionType.Equal
                    && outerUnary.Operand is SqlConstantExpression innerConstantNull1
                    && innerConstantNull1.Value == null)
                {
                    return _sqlExpressionFactory.Constant(true, outerUnary.TypeMapping);
                }

                // NULL IS NOT NULL -> false
                if (outerUnary.OperatorType == ExpressionType.NotEqual
                    && outerUnary.Operand is SqlConstantExpression innerConstantNull2
                    && innerConstantNull2.Value == null)
                {
                    return _sqlExpressionFactory.Constant(false, outerUnary.TypeMapping);
                }

                if (outerUnary.Operand is SqlUnaryExpression innerUnary)
                {
                    if (outerUnary.OperatorType == ExpressionType.Not)
                    {
                        // !(!a) -> a
                        if (innerUnary.OperatorType == ExpressionType.Not)
                        {
                            return Visit(innerUnary.Operand);
                        }

                        if (innerUnary.OperatorType == ExpressionType.Equal)
                        {
                            //!(a IS NULL) -> a IS NOT NULL
                            return Visit(_sqlExpressionFactory.IsNotNull(innerUnary.Operand));
                        }

                        //!(a IS NOT NULL) -> a IS NULL
                        if (innerUnary.OperatorType == ExpressionType.NotEqual)
                        {
                            return Visit(_sqlExpressionFactory.IsNull(innerUnary.Operand));
                        }
                    }

                    // (!a) IS NULL <==> a IS NULL
                    if (outerUnary.OperatorType == ExpressionType.Equal
                        && innerUnary.OperatorType == ExpressionType.Not)
                    {
                        return Visit(_sqlExpressionFactory.IsNull(innerUnary.Operand));
                    }

                    // (!a) IS NOT NULL <==> a IS NOT NULL
                    if (outerUnary.OperatorType == ExpressionType.NotEqual
                        && innerUnary.OperatorType == ExpressionType.Not)
                    {
                        return Visit(_sqlExpressionFactory.IsNotNull(innerUnary.Operand));
                    }
                }

                if (outerUnary.Operand is SqlBinaryExpression innerBinary)
                {
                    // De Morgan's
                    if (innerBinary.OperatorType == ExpressionType.AndAlso
                        || innerBinary.OperatorType == ExpressionType.OrElse)
                    {
                        var newLeft = (SqlExpression)Visit(_sqlExpressionFactory.Not(innerBinary.Left));
                        var newRight = (SqlExpression)Visit(_sqlExpressionFactory.Not(innerBinary.Right));

                        return innerBinary.OperatorType == ExpressionType.AndAlso
                            ? _sqlExpressionFactory.OrElse(newLeft, newRight)
                            : _sqlExpressionFactory.AndAlso(newLeft, newRight);
                    }

                    // note that those optimizations are only valid in 2-value logic
                    // they are safe to do here because null semantics removes possibility of nulls in the tree
                    // however if we decide to do "partial" null semantics (that doesn't distinguish between NULL and FALSE, e.g. for predicates)
                    // we need to be extra careful here
                    if (_expressionTypesNegationMap.ContainsKey(innerBinary.OperatorType))
                    {
                        return Visit(
                            _sqlExpressionFactory.MakeBinary(
                                _expressionTypesNegationMap[innerBinary.OperatorType],
                                innerBinary.Left,
                                innerBinary.Right,
                                innerBinary.TypeMapping));
                    }
                }
            }

            if (extensionExpression is SqlBinaryExpression outerBinary)
            {
                if (outerBinary.OperatorType == ExpressionType.AndAlso
                    || outerBinary.OperatorType == ExpressionType.OrElse)
                {
                    var newLeft = (SqlExpression)Visit(outerBinary.Left);
                    var newRight = (SqlExpression)Visit(outerBinary.Right);

                    var newLeftConstant = newLeft as SqlConstantExpression;
                    var newRightConstant = newRight as SqlConstantExpression;
                    if (newLeftConstant != null || newRightConstant != null)
                    {
                        // true && a -> a
                        // true || a -> true
                        // false && a -> false
                        // false || a -> a 
                        if (newLeftConstant != null)
                        {
                            return outerBinary.OperatorType == ExpressionType.AndAlso
                                ? (bool)newLeftConstant.Value
                                    ? newRight
                                    : newLeftConstant
                                : (bool)newLeftConstant.Value
                                    ? newLeftConstant
                                    : newRight;
                        }
                        else
                        {
                            // a && true -> a
                            // a || true -> true 
                            // a && false -> false
                            // a || false -> a
                            return outerBinary.OperatorType == ExpressionType.AndAlso
                                ? (bool)newRightConstant.Value
                                    ? newLeft
                                    : newRightConstant
                                : (bool)newRightConstant.Value
                                    ? newRightConstant
                                    : newLeft;
                        }
                    }
                }
            }

            return base.VisitExtension(extensionExpression);
        }
    }
}
