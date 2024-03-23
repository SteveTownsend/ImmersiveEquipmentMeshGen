using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda.Synthesis.Settings;

namespace ImmersiveEquipmentDisplay
{
    public class Meshes
    {
        [SynthesisSettingName("Input Folder")]
        [SynthesisTooltip("This must be a valid path on your computer. Leave blank to use Game Data location in your Mod Manager VFS. Can use relative path to current directory, which is usually the VFS Game Data location. The suffix 'meshes/', where NIF files are read in-game, is added in the patcher and not needed here. Relative or absolute path is allowed.")]
        [SynthesisDescription("Path to search for Weapon and Armour meshes.")]
        public string InputFolder { get; set; } = "";

        [SynthesisSettingName("Output Folder")]
        [SynthesisTooltip("This must be a valid path on your computer. Typically this points to a new mod directory in your Mod Manager VFS, e.g. 'D:/ModdedSkyrim/mods/Immersive Equipment Display Output'. The suffix 'meshes/', where NIF files are read in-game, is added in the patcher and not needed here. Relative or absolute path is allowed.")]
        [SynthesisDescription("Path where transformed Weapon and Armour meshes are written.")]
        public string OutputFolder { get; set; } = "";
        //public string OutputFolder { get; set; } = "j:/omegalotd/tools/mods/Immersive Equipment Display Output";

        [SynthesisSettingName("Process Two-Handed Weapon Meshes")]
        [SynthesisTooltip("For use with mods which may change 2H weapon grips to 1H (CGO for example).")]
        [SynthesisDescription("Create left scabbards for 2H weapon meshes.")]
        public bool Accept2HWeapons { get; set; } = false;

        private List<string[]> _nifBlackList = new List<string[]>();
        private static List<string> DefaultBlackList()
        {
            var defaults = new List<string>();
            return defaults;
        }
        private static readonly List<string[]> _defaultNifBlackList = ParseNifFilters(DefaultBlackList());
        [SynthesisSettingName("BlackList Patterns")]
        [SynthesisTooltip("Each entry is a comma-separated list of strings. Every string must match for a mesh to be excluded. A mesh that matches a BlackList entry cannot be WhiteListed.")]
        [SynthesisDescription("List of patterns for excluded mesh names.")]
        public List<string> NifBlackList
        {
            get { return NifFilters.BuildNifFilters(_nifBlackList); }
            set { _nifBlackList = NifFilters.ParseNifFilters(value); }
        }
        private List<string[]>? _fullBlackList;
        private List<string[]> fullBlackList
        {
            get
            {
                if (_fullBlackList is null)
                    _fullBlackList = new List<string[]>(_nifBlackList.Concat(_defaultNifBlackList));
                return _fullBlackList;
            }
        }

        private List<string[]> _nifWhiteList = new List<string[]>();
        [SynthesisSettingName("WhiteList Patterns")]
        [SynthesisTooltip("Each entry is a comma-separated list of strings. Every string must match for a non-BlackListed mesh to be included.")]
        [SynthesisDescription("List of patterns for included mesh names.")]
        public List<string> NifWhiteList
        {
            get { return BuildNifFilters(_nifWhiteList); }
            set { _nifWhiteList = ParseNifFilters(value); }
        }

        private static List<string[]> ParseNifFilters(IList<string> filterData)
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

        private static List<string> BuildNifFilters(IList<string[]> filters)
        {
            List<string> nifFilters = new List<string>();
            foreach (string[] filter in filters)
            {
                nifFilters.Add(String.Join(',', filter));
            }
            return nifFilters;
        }

        public bool IsNifValid(string nifPath)
        {
            // check blacklist, exclude NIF if all substrings in an entry match
            foreach (string[] filterElements in fullBlackList)
            {
                if (filterElements
                    .Where(x => !string.IsNullOrEmpty(x))
                    .All(v => nifPath.Contains(v, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }
            // if not blacklisted, check whitelist if present
            if (_nifWhiteList.Count == 0)
            {
                // allow all iff no filters
                return true;
            }
            foreach (string[] filterElements in _nifWhiteList)
            {
                if (filterElements
                    .Where(x => !string.IsNullOrEmpty(x))
                    .All(v => nifPath.Contains(v, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;  // disallow all if none of the >= 1 whitelist filters matched
        }

        public List<string> GetConfigErrors()
        {
            List<string> errors = new List<string>();
            try
            {
                if (String.IsNullOrEmpty(InputFolder))
                {
                    InputFolder = ScriptLess.PatcherState.DataFolderPath + '/';
                }
                InputFolder = Helper.EnsureInputPathIsValid(InputFolder);
            }
            catch (Exception e)
            {
                errors.Add(e.GetBaseException().ToString());
            }
            try
            {
                OutputFolder = Helper.EnsureOutputPathIsValid(OutputFolder);
            }
            catch (Exception e)
            {
                errors.Add(e.GetBaseException().ToString());
            }
            if (InputFolder == OutputFolder)
            {
                errors.Add(String.Format("Mesh Generation cannot use {0} as both Input and Output Folder", InputFolder));
            }
            return errors;
        }
    }
}
