using System;
using System.Collections.Generic;
using System.Linq;

namespace HQ.Common.Extensions
{
    internal static class EnumerableExtensions
    {
        public static SelfEnumerable<T> SelfEnumerate<T>(this List<T> inner)
        {
            return new SelfEnumerable<T>(inner);
        }

        public static FuncEnumerable<T, TResult> Enumerate<T, TResult>(this List<T> inner, Func<T, TResult> func)
        {
            return new FuncEnumerable<T, TResult>(inner, func);
        }

        public static PredicateEnumerable<T> Enumerate<T>(this List<T> inner, Predicate<T> predicate)
        {
            return new PredicateEnumerable<T>(inner, predicate);
        }

        /// <summary> Kahn's algorithm: https://en.wikipedia.org/wiki/Topological_sorting</summary>
        public static List<T> TopologicalSort<T>(this List<T> nodes, List<Tuple<T, T>> edges) where T : IEquatable<T>
        {
            /*
                L ← Empty list that will contain the sorted elements
                S ← Set of all nodes with no incoming edge
                while S is non-empty do
                    remove a node n from S
                    add n to tail of L
                    for each node m with an edge e from n to m do
                        remove edge e from the graph
                        if m has no other incoming edges then
                            insert m into S
                if graph has edges then
                    return error   (graph has at least one cycle)
                else 
                    return L   (a topologically sorted order)
             */

            var sorted = new List<T>(nodes.Count);
            var set = new HashSet<T>();

            foreach (var node in nodes.Enumerate<T>(n => All(edges, n)))
                set.Add(node);

            while (set.Count > 0)
            {
                var node = set.ElementAt(0);
                set.Remove(node);
                sorted.Add(node);

                foreach (var e in edges.Enumerate<Tuple<T, T>>(e => e.Item1.Equals(node)))
                {
                    var m = e.Item2;
                    edges.Remove(e);

                    var all = true;
                    foreach (var me in edges)
                    {
                        if (!me.Item2.Equals(m))
                            continue;
                        all = false;
                        break;
                    }

                    if (all)
                    {
                        set.Add(m);
                    }
                }
            }

            return edges.Count > 0 ? null : sorted;
        }

        // ReSharper disable once ParameterTypeCanBeEnumerable.Local
        private static bool All<T>(List<Tuple<T, T>> edges, T n) where T : IEquatable<T>
        {
            var all = true;
            foreach (var e in edges)
            {
                if (!e.Item2.Equals(n))
                    continue;
                all = false;
                break;
            }
            return all;
        }
    }
}
