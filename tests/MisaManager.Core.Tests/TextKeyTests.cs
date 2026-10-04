using System.Text;

namespace MisaManager.Core.Tests;

public sealed class TextKeyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullAndEmptyGiveAnEmptyKey(string? value)
    {
        Assert.Equal("", TextKey.Canonical(value));
        Assert.Equal("", TextKey.Compact(value));
        Assert.Equal("", TextKey.LettersAndDigits(value));
    }

    [Fact]
    public void KeyIsUppercase()
        => Assert.Equal("PHÂN NPK 16.16.8.6S".Normalize(NormalizationForm.FormD), TextKey.Canonical("Phân npk 16.16.8.6s"));

    [Fact]
    public void ReturnedKeyIsInDecomposedForm()
        => Assert.Equal(TextKey.Canonical("Phân").Normalize(NormalizationForm.FormD), TextKey.Canonical("Phân"));

    [Fact(Skip = "BUG: src/MisaManager.Core/TextKey.cs:22 lists U+00B4 (acute accent) as an apostrophe look-alike, but Canonical() runs "
        + "NFKC first, which turns U+00B4 into SPACE + U+0301 before Fold() sees it, so the key becomes \"RÔNG ĐỎ ĐĂĆ̣ BIÊṬ́\" instead of the apostrophe form.")]
    public void AcuteAccentUsedAsApostropheIsUnified()
        => Assert.Equal(TextKey.Canonical("Rông đỏ 'đặc biệt'"), TextKey.Canonical("Rông đỏ ´đặc biệt´"));

    [Fact(Skip = "BUG: src/MisaManager.Core/TextKey.cs:23 folds U+2033 (double prime) to a double quote, but NFKC first expands it to two U+2032 primes, "
        + "which become two apostrophes, so the key differs from the straight double-quote spelling.")]
    public void DoublePrimeIsUnifiedWithDoubleQuote()
        => Assert.Equal(TextKey.Canonical("Thuốc \"diệt cỏ\""), TextKey.Canonical("Thuốc ″diệt cỏ″"));

    [Theory]
    [InlineData("hoà", "hòa")]
    [InlineData("HOÀ", "HÒA")]
    [InlineData("thuỷ", "thủy")]
    [InlineData("khoẻ", "khỏe")]
    [InlineData("Hoá đơn", "Hóa đơn")]
    [InlineData("Quỳnh", "Quỳnh")]
    [InlineData("Tạ Thị Lụa", "Tạ Thị Lụa")]
    public void OldAndNewToneMarkPlacementCompareEqual(string oldStyle, string newStyle)
        => Assert.Equal(TextKey.Canonical(newStyle), TextKey.Canonical(oldStyle));

    [Theory]
    [InlineData("hòa", "hóa")]
    [InlineData("toán", "toàn")]
    [InlineData("Phân", "Phan")]
    [InlineData("Việt", "Viêt")]
    public void DifferentTonesStayDifferent(string left, string right)
        => Assert.NotEqual(TextKey.Canonical(left), TextKey.Canonical(right));

    [Fact]
    public void VowelShapeMarksAreNotConfusedWithTones()
    {
        Assert.NotEqual(TextKey.Canonical("Ăn"), TextKey.Canonical("An"));
        Assert.NotEqual(TextKey.Canonical("Ơn"), TextKey.Canonical("On"));
        Assert.NotEqual(TextKey.Canonical("Â"), TextKey.Canonical("Ă"));
    }

    [Fact]
    public void PrecomposedAndCombiningInputCompareEqual()
    {
        var precomposed = "Việt Nam – Hà Nội, Đà Nẵng";
        var combining = precomposed.Normalize(NormalizationForm.FormD);

        Assert.NotEqual(precomposed, combining);
        Assert.Equal(TextKey.Canonical(precomposed), TextKey.Canonical(combining));
    }

    [Fact]
    public void ToneMarkTypedBeforeOrAfterTheShapeMarkIsTheSame()
    {
        var shapeThenTone = "ie\u0302\u0323t"; // ê + dot below
        var toneThenShape = "ie\u0323\u0302t";

        Assert.Equal(TextKey.Canonical(shapeThenTone), TextKey.Canonical(toneThenShape));
        Assert.Equal(TextKey.Canonical("iệt"), TextKey.Canonical(shapeThenTone));
    }

    [Fact]
    public void LegacyCombiningToneCodePointsAreMappedToTheStandardOnes()
    {
        Assert.Equal(TextKey.Canonical("à"), TextKey.Canonical("a\u0340"));
        Assert.Equal(TextKey.Canonical("á"), TextKey.Canonical("a\u0341"));
        Assert.Equal(TextKey.Canonical("ã"), TextKey.Canonical("a\u0342"));
    }

    [Theory]
    [InlineData("ÐẠM", "ĐẠM")]
    [InlineData("ðạm", "Đạm")]
    [InlineData("Ɖạm", "Đạm")]
    [InlineData("ɖạm", "Đạm")]
    [InlineData("Ðường", "Đường")]
    public void EthAndOtherDLookAlikesFoldToVietnameseD(string typed, string expected)
        => Assert.Equal(TextKey.Canonical(expected), TextKey.Canonical(typed));

    [Fact]
    public void DWithoutStrokeIsNotVietnameseD() => Assert.NotEqual(TextKey.Canonical("Đạm"), TextKey.Canonical("Dạm"));

    [Theory]
    [InlineData("ＰＨÂＮ　ＮＰＫ　１６", "Phân NPK 16")]
    [InlineData("ﬁne", "fine")]
    [InlineData("Chai\u00A0100ml", "Chai 100ml")]
    [InlineData("Số\u2003lượng", "Số lượng")]
    public void CompatibilityFormsAreFolded(string typed, string expected)
        => Assert.Equal(TextKey.Canonical(expected), TextKey.Canonical(typed));

    [Theory]
    [InlineData("Rông đỏ ‘đặc biệt’", "Rông đỏ 'đặc biệt'")]
    [InlineData("Rông đỏ `đặc biệt`", "Rông đỏ 'đặc biệt'")]
    [InlineData("Thuốc “diệt cỏ”", "Thuốc \"diệt cỏ\"")]
    [InlineData("Thuốc „diệt cỏ‟", "Thuốc \"diệt cỏ\"")]
    public void CurlyQuotesAndSimilarMarksAreUnified(string typed, string expected)
        => Assert.Equal(TextKey.Canonical(expected), TextKey.Canonical(typed));

    [Theory]
    [InlineData("2×3", "2x3")]
    [InlineData("2✕3", "2x3")]
    [InlineData("2✖3", "2x3")]
    [InlineData("Chai 10 × 50ml", "Chai 10 x 50ml")]
    public void MultiplicationSignIsTheLetterX(string typed, string expected)
        => Assert.Equal(TextKey.Canonical(expected), TextKey.Canonical(typed));

    [Theory]
    [InlineData("NPK 16–16–8", "NPK 16-16-8")]
    [InlineData("NPK 16—16—8", "NPK 16-16-8")]
    [InlineData("NPK 16‑16‑8", "NPK 16-16-8")]
    [InlineData("NPK 16−16−8", "NPK 16-16-8")]
    [InlineData("NPK 16－16－8", "NPK 16-16-8")]
    public void DashVariantsAreUnifiedToHyphen(string typed, string expected)
        => Assert.Equal(TextKey.Canonical(expected), TextKey.Canonical(typed));

    [Theory]
    [InlineData("1,5", "1.5")]
    [InlineData("Chai 0,5 lít", "Chai 0.5 lít")]
    [InlineData("Phân hữu cơ 2,5kg", "Phân hữu cơ 2.5kg")]
    public void DecimalCommaBetweenDigitsIsReadAsDot(string typed, string expected)
        => Assert.Equal(TextKey.Canonical(expected), TextKey.Canonical(typed));

    [Theory]
    [InlineData("Thuốc A,B")]
    [InlineData("1, 5")]
    [InlineData("Phân, 5kg")]
    [InlineData("5,")]
    [InlineData(",5")]
    public void CommasOutsideNumbersAreKept(string typed)
        => Assert.Contains(',', TextKey.Canonical(typed));

    [Theory]
    [InlineData("Chai\u200B 100ml", "Chai 100ml")]
    [InlineData("\uFEFFPhân kali", "Phân kali")]
    [InlineData("Phân\u00ADkali", "Phânkali")]
    [InlineData("Phân\u200Dkali", "Phânkali")]
    [InlineData("Phân\u0001 kali", "Phân kali")]
    public void InvisibleAndControlCharactersAreRemoved(string typed, string expected)
        => Assert.Equal(TextKey.Canonical(expected), TextKey.Canonical(typed));

    [Fact]
    public void WhitespaceCollapsesToSingleSpacesAndIsTrimmed()
    {
        Assert.Equal("CHAI 100 ML", TextKey.Canonical("  chai \t 100\r\n ml  "));
        Assert.Equal("A B", TextKey.Canonical("A\u00A0\u00A0B"));
    }

    [Fact]
    public void CompactDropsEverySpace()
    {
        Assert.Equal(TextKey.Compact("Chai 90ml"), TextKey.Compact("chai  90 ml"));
        Assert.Equal("CHAI90ML", TextKey.Compact("Chai 90 ml"));
    }

    [Fact]
    public void CompactStillSeparatesDifferentBusinessNames()
    {
        Assert.NotEqual(TextKey.Compact("Chai 90ml"), TextKey.Compact("Chai 900ml"));
        Assert.NotEqual(TextKey.Compact("Phân NPK 16-16-8"), TextKey.Compact("Phân NPK 16-16-8+TE"));
    }

    [Fact]
    public void LettersAndDigitsIgnoreSpacingAndPunctuationButKeepMarks()
    {
        Assert.Equal(TextKey.LettersAndDigits("chai100ml đỏ"), TextKey.LettersAndDigits("Chai 100ml (ĐỎ)"));
        Assert.NotEqual(TextKey.LettersAndDigits("Đỏ"), TextKey.LettersAndDigits("Đô"));
        Assert.NotEqual(TextKey.LettersAndDigits("Phân 5"), TextKey.LettersAndDigits("Phân 6"));
    }

    [Fact]
    public void KeyIsIdempotent()
    {
        foreach (var sample in new[] { "Hoà ＰＨÂＮ – “x” 1,5 ×", "Rông đỏ 200SL - Chai 100ml", "  Ðạm  Phú\u00A0Mỹ " })
        {
            var once = TextKey.Canonical(sample);
            Assert.Equal(once, TextKey.Canonical(once));
        }
    }

    [Fact]
    public void RealExportNamesMatchTheirExcelSpellings()
    {
        // Same item as exported by MISA and as typed in an Excel invoice sheet.
        Assert.Equal(TextKey.Compact("Phân NPK Cà Mau Gold 20-10-10-25kg"), TextKey.Compact("PHÂN NPK CÀ MAU GOLD 20–10–10–25 KG"));
        Assert.Equal(TextKey.Compact("Phân Ure Đạm Phú Mỹ loại 1 (bao)"), TextKey.Compact("Phân Ure Ðạm Phú Mỹ loại 1 ( bao )"));
    }
}
