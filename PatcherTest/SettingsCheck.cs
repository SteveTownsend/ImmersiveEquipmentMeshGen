using System;
using Xunit;
using AllGUD;

namespace PatcherTest
{
    public class SettingsCheck
    {
        [Fact]
        public void Paths()
        {
            Assert.Empty(Helper.EnsureInputPathIsValid(""));
            Assert.NotEmpty(Helper.EnsureInputPathIsValid("."));

            Assert.Equal(Helper.EnsureInputPathIsValid("C:"), Helper.EnsureInputPathIsValid("C:\\"));
            Assert.Equal(Helper.EnsureInputPathIsValid("C:"), Helper.EnsureInputPathIsValid("C:/"));

            Assert.Throws<ArgumentException>(() => Helper.EnsureInputPathIsValid("C:\\OKsofar\\Not\tSoMuch"));
            Assert.Throws<ArgumentException>(() => Helper.EnsureInputPathIsValid("\\\\MyUNCName\\MyUNCDir"));
            Assert.Throws<ArgumentException>(() => Helper.EnsureInputPathIsValid("U:\\InvalidDrive"));

            Assert.Empty(Helper.EnsureOutputPathIsValid(""));
            Assert.NotEmpty(Helper.EnsureOutputPathIsValid("."));

            Assert.Throws<ArgumentException>(() => Helper.EnsureOutputPathIsValid("C:\\OKsofar\\Not\tSoMuch"));
            Assert.Throws<ArgumentException>(() => Helper.EnsureOutputPathIsValid("\\\\MyUNCName\\MyUNCDir"));
            Assert.Throws<ArgumentException>(() => Helper.EnsureOutputPathIsValid("U:\\InvalidDrive"));
        }

        [Fact]
        public void Files()
        {
            string fileSuffix = "/ValidLog.txt";
            Assert.EndsWith(fileSuffix, Helper.EnsureOutputFileIsValid("." + fileSuffix));
            Assert.Empty(Helper.EnsureOutputFileIsValid(""));

            Assert.Throws<ArgumentException>(() => Helper.EnsureOutputFileIsValid("./SomeJunkDirectory" + fileSuffix));
            Assert.Throws<ArgumentException>(() => Helper.EnsureOutputFileIsValid("."));
            Assert.Throws<ArgumentException>(() => Helper.EnsureOutputFileIsValid("C:\\OKsofar\\Not\tSoMuch\\" + fileSuffix));
            Assert.Throws<ArgumentException>(() => Helper.EnsureOutputFileIsValid("\\\\MyUNCName\\MyUNCDir\\" + fileSuffix));
            Assert.Throws<ArgumentException>(() => Helper.EnsureInputPathIsValid("U:\\InvalidDrive" + fileSuffix));
        }
    }
}
