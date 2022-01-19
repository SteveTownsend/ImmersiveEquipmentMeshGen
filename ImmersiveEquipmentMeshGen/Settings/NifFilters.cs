using System;
using System.Collections.Generic;

namespace AllGUD
{
    public class NifFilters
    {
        public static List<string[]> ParseNifFilters(IList<string> filterData)
        {
            List<string[]> nifFilter = new List<string[]>();
            foreach (string filter in filterData)
            {
                if (!String.IsNullOrEmpty(filter))
                {
                    string[] filterElements = filter.Split(',');
                    if (filterElements.Length > 0)
                    {
                        nifFilter.Add(filterElements);
                    }
                }
            }
            return nifFilter;
        }

        public static List<string> BuildNifFilters(IList<string[]> filters)
        {
            List<string> nifFilters = new List<string>();
            foreach (string[] filter in filters)
            {
                nifFilters.Add(String.Join(',', filter));
            }
            return nifFilters;
        }
    }
}
