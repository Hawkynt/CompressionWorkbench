#pragma warning disable CS1591
using Compression.Registry.Layout;

namespace Compression.Tests.Layout;

[TestFixture]
public class DefragSortKeyParseTests {

  [Test]
  public void BareField_DefaultsToAscending() {
    var key = DefragSortKey.Parse("name");
    Assert.That(key.Field, Is.EqualTo(DefragSortField.Name));
    Assert.That(key.Direction, Is.EqualTo(SortDirection.Ascending));
  }

  [Test]
  public void ExplicitAscending_Parses() {
    var key = DefragSortKey.Parse("size asc");
    Assert.That(key.Field, Is.EqualTo(DefragSortField.Size));
    Assert.That(key.Direction, Is.EqualTo(SortDirection.Ascending));
  }

  [Test]
  public void ExplicitDescending_Parses() {
    var key = DefragSortKey.Parse("lastModified desc");
    Assert.That(key.Field, Is.EqualTo(DefragSortField.LastModified));
    Assert.That(key.Direction, Is.EqualTo(SortDirection.Descending));
  }

  [TestCase("name", DefragSortField.Name)]
  [TestCase("Name", DefragSortField.Name)]
  [TestCase("NAME", DefragSortField.Name)]
  [TestCase("path", DefragSortField.Path)]
  [TestCase("extension", DefragSortField.Extension)]
  [TestCase("ext", DefragSortField.Extension)]
  [TestCase("size", DefragSortField.Size)]
  [TestCase("length", DefragSortField.Size)]
  [TestCase("lastModified", DefragSortField.LastModified)]
  [TestCase("last_modified", DefragSortField.LastModified)]
  [TestCase("last-modified", DefragSortField.LastModified)]
  [TestCase("LastModified", DefragSortField.LastModified)]
  [TestCase("mtime", DefragSortField.LastModified)]
  [TestCase("modified", DefragSortField.LastModified)]
  [TestCase("lastAccessed", DefragSortField.LastAccessed)]
  [TestCase("atime", DefragSortField.LastAccessed)]
  [TestCase("created", DefragSortField.Created)]
  [TestCase("ctime", DefragSortField.Created)]
  [TestCase("attributes", DefragSortField.Attributes)]
  [TestCase("attrs", DefragSortField.Attributes)]
  [TestCase("attr", DefragSortField.Attributes)]
  public void FieldNames_AcceptVariousCasings(string input, DefragSortField expected) {
    Assert.That(DefragSortKey.Parse(input).Field, Is.EqualTo(expected));
  }

  [TestCase("name ascending", SortDirection.Ascending)]
  [TestCase("name DESC", SortDirection.Descending)]
  [TestCase("name descending", SortDirection.Descending)]
  [TestCase("name up", SortDirection.Ascending)]
  [TestCase("name down", SortDirection.Descending)]
  [TestCase("name +", SortDirection.Ascending)]
  [TestCase("name -", SortDirection.Descending)]
  public void Directions_AcceptVariousForms(string input, SortDirection expected) {
    Assert.That(DefragSortKey.Parse(input).Direction, Is.EqualTo(expected));
  }

  [Test]
  public void ToString_RoundTrips() {
    var original = new DefragSortKey(DefragSortField.LastModified, SortDirection.Descending);
    var roundTripped = DefragSortKey.Parse(original.ToString());
    Assert.That(roundTripped, Is.EqualTo(original));
  }

  [Test]
  public void ToString_AscendingIncludesAscSuffix() {
    var s = new DefragSortKey(DefragSortField.Name, SortDirection.Ascending).ToString();
    Assert.That(s, Is.EqualTo("name asc"));
  }

  [Test]
  public void ToString_DescendingIncludesDescSuffix() {
    var s = new DefragSortKey(DefragSortField.Size, SortDirection.Descending).ToString();
    Assert.That(s, Is.EqualTo("size desc"));
  }

  [Test]
  public void UnknownField_Throws() {
    Assert.Throws<FormatException>(() => DefragSortKey.Parse("foobar"));
  }

  [Test]
  public void UnknownDirection_Throws() {
    Assert.Throws<FormatException>(() => DefragSortKey.Parse("name sideways"));
  }

  [Test]
  public void EmptyString_Throws() {
    Assert.Throws<FormatException>(() => DefragSortKey.Parse("   "));
  }

  [Test]
  public void NullString_Throws() {
    Assert.Throws<ArgumentNullException>(() => DefragSortKey.Parse(null!));
  }
}
