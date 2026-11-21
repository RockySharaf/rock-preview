using Anything.Core.Search;

namespace Anything.Tests.Search;

public class NameOrderTests
{
    static int Sign(string a, string b) => Math.Sign(NameOrder.Compare(a, b));

    [Theory]
    [InlineData("IMG_2", "IMG_10")]
    [InlineData("IMG_10", "IMG_100")]
    [InlineData("file2.txt", "file10.txt")]
    [InlineData("file9.txt", "File10.txt")]
    [InlineData("a1b2", "a1b10")]
    [InlineData("2", "10")]
    [InlineData("v1.9", "v1.10")]
    [InlineData("IMG.jpg", "IMG_1.jpg")]   // '.' before '_', as before
    [InlineData("IMG_1", "IMG_A")]         // digits before letters, as before
    [InlineData("a 1", "a1")]              // a space before a digit, as before
    public void Numbers_compare_by_value(string smaller, string larger)
    {
        Assert.Equal(-1, Sign(smaller, larger));
        Assert.Equal(1, Sign(larger, smaller));
    }

    [Fact]
    public void Leading_zeros_count_only_when_nothing_else_differs()
    {
        Assert.Equal(-1, Sign("file1", "file01"));
        Assert.Equal(-1, Sign("file01", "file001"));
        Assert.Equal(-1, Sign("file01", "file2"));   // the value first
        Assert.Equal(-1, Sign("file01a", "file1b")); // then what follows
        Assert.Equal(-1, Sign("file007", "file8"));
        Assert.Equal(0, Sign("file0", "file0"));
        Assert.Equal(-1, Sign("file0", "file00"));
    }

    [Fact]
    public void Very_long_digit_runs_do_not_overflow()
    {
        string big = new('9', 40), bigger = "1" + new string('0', 40);
        Assert.Equal(-1, Sign("n" + big, "n" + bigger));
        Assert.Equal(-1, Sign("n" + new string('0', 30) + "5", "n" + big));
        Assert.Equal(-1, Sign("n18446744073709551615", "n18446744073709551616")); // past ulong
    }

    [Theory]
    [InlineData("alpha", "Beta")]
    [InlineData("Beta", "gamma")]
    [InlineData("abc", "abcd")]
    [InlineData("a-b", "a_b")]
    public void Names_without_digits_keep_the_ignore_case_order(string a, string b)
    {
        Assert.Equal(Math.Sign(string.Compare(a, b, StringComparison.OrdinalIgnoreCase)), Sign(a, b));
        Assert.Equal(0, Sign("README", "readme"));
        Assert.NotEqual(0, NameOrder.CompareWithTieBreak("README", "readme"));
    }

    [Fact]
    public void Is_a_valid_total_order_and_agrees_with_the_prefix_keys()
    {
        var rnd = new Random(7);
        string[] parts = ["a", "A", "img", "IMG_", "_", ".", " ", "-", "0", "00", "1", "01", "2", "9", "10", "010", "99", "100", "x", "é", "会", "z"];
        var names = new List<string>();
        for (int i = 0; i < 300; i++)
        {
            int n = rnd.Next(1, 7);
            var s = "";
            for (int k = 0; k < n; k++) s += parts[rnd.Next(parts.Length)];
            names.Add(s);
        }
        var arr = names.ToArray();
        for (int i = 0; i < arr.Length; i++)
            for (int j = 0; j < arr.Length; j++)
            {
                int ab = NameOrder.CompareWithTieBreak(arr[i], arr[j]), ba = NameOrder.CompareWithTieBreak(arr[j], arr[i]);
                Assert.Equal(Math.Sign(ab), -Math.Sign(ba));
                if (ab == 0) Assert.Equal(arr[i], arr[j]);

                // The packed prefix keys never contradict the full comparison.
                NameOrder.Prefix(arr[i], out ulong a0, out ulong a1, out ulong a2);
                NameOrder.Prefix(arr[j], out ulong b0, out ulong b1, out ulong b2);
                int p = a0 != b0 ? a0.CompareTo(b0) : a1 != b1 ? a1.CompareTo(b1) : a2.CompareTo(b2);
                if (p != 0) Assert.True(Math.Sign(p) == Math.Sign(NameOrder.Compare(arr[i], arr[j])), $"prefix disagrees: '{arr[i]}' '{arr[j]}'");
            }

        // Transitivity: a sort with this comparer leaves every adjacent and every sampled pair in order.
        var sorted = arr.OrderBy(x => rnd.Next()).ToArray();
        Array.Sort(sorted, NameOrder.CompareWithTieBreak);
        for (int i = 1; i < sorted.Length; i++) Assert.True(NameOrder.CompareWithTieBreak(sorted[i - 1], sorted[i]) <= 0);
        for (int t = 0; t < 5000; t++)
        {
            int x = rnd.Next(sorted.Length), y = rnd.Next(sorted.Length), z = rnd.Next(sorted.Length);
            string a = sorted[x], b = sorted[y], c = sorted[z];
            if (NameOrder.CompareWithTieBreak(a, b) <= 0 && NameOrder.CompareWithTieBreak(b, c) <= 0)
                Assert.True(NameOrder.CompareWithTieBreak(a, c) <= 0, $"not transitive: '{a}' '{b}' '{c}'");
        }
    }

    static int PrefixOrder(string a, string b)
    {
        NameOrder.Prefix(a, out ulong a0, out ulong a1, out ulong a2);
        NameOrder.Prefix(b, out ulong b0, out ulong b1, out ulong b2);
        return Math.Sign(a0 != b0 ? a0.CompareTo(b0) : a1 != b1 ? a1.CompareTo(b1) : a2.CompareTo(b2));
    }

    [Theory]
    [InlineData("IMG_0001.JPG", "IMG_0002.JPG")]
    [InlineData("IMG_2.jpg", "IMG_10.jpg")]
    [InlineData("photo 2023.jpg", "photo 2024.jpg")]
    [InlineData("001 Track.mp3", "002 Track.mp3")]
    [InlineData("a1b2", "a1b10")]
    [InlineData("v1.9", "v1.10")]
    [InlineData("file", "file0")]
    public void The_prefix_keys_already_separate_names_whose_digit_runs_differ(string smaller, string larger)
    {
        Assert.Equal(-1, PrefixOrder(smaller, larger));
        Assert.Equal(1, PrefixOrder(larger, smaller));
    }

    [Fact]
    public void Names_differing_only_in_leading_zeros_tie_on_the_prefix_keys()
    {
        Assert.Equal(0, PrefixOrder("file1", "file01"));
        Assert.Equal(0, PrefixOrder("001", "1"));
        Assert.Equal(-1, Sign("1", "001"));
    }

    [Fact]
    public void Long_names_with_digit_runs_agree_with_the_prefix_keys()
    {
        var rnd = new Random(11);
        string[] parts = ["IMG_", "DSC", "_", ".", " ", "-", "a", "Z", "0", "00", "0001", "0002", "1", "2", "9", "10", "99", "100",
            "123456", "20240101", "18446744073709551616", "jpg", "é", "会", "x"];
        var names = new List<string>();
        for (int i = 0; i < 400; i++)
        {
            int n = rnd.Next(2, 9);
            var s = "";
            for (int k = 0; k < n; k++) s += parts[rnd.Next(parts.Length)];
            names.Add(s);
        }
        foreach (var a in names)
            foreach (var b in names)
            {
                int p = PrefixOrder(a, b);
                if (p != 0) Assert.True(p == Sign(a, b), $"prefix disagrees: '{a}' '{b}'");
            }
    }
}
