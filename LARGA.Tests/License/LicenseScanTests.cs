using LARGA.SharedCore.Services;

namespace LARGA.Tests.License;

/// <summary>
/// The driver's license scan (LtoLicenseParser.ParseBest) against what ML Kit really read on the
/// emulator (Fixtures/License, one section per LicenseScanPipeline pass): tests/license2.jfif
/// (clean), tests/license.jfif (the shiny seal's glare over the License No.), and darker,
/// blurred, faded and shadowed copies of the clean photo.
/// </summary>
public class LicenseScanTests
{
    private const string Registered = "Alexa Rose Miñoza";

    private static List<string> Passes(string fixture)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "License", fixture);
        var passes = new List<string>();
        var current = new List<string>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith('#'))
            {
                continue;
            }
            if (line.StartsWith("=== pass", StringComparison.Ordinal))
            {
                if (current.Count > 0)
                {
                    passes.Add(string.Join("\n", current));
                }
                current.Clear();
                continue;
            }
            current.Add(line);
        }
        if (current.Count > 0)
        {
            passes.Add(string.Join("\n", current));
        }

        Assert.NotEmpty(passes);
        return passes;
    }

    [Fact]
    public void Clean_card_reads_every_field_in_one_pass()
    {
        var r = LtoLicenseParser.ParseBest(Passes("mlkit_lic_clean.txt"));

        // ML Kit reads the zero in "G01" as the letter O ("GO1-25-009637") - this used to leave
        // the number blank on the phone even for a perfect photo.
        Assert.Equal("G01-25-009637", r.LicenseNumber);
        Assert.False(r.LicenseNumberUncertain);
        Assert.Equal(new DateTime(2030, 9, 27), r.ExpiryDate);
        Assert.False(r.ExpiryUncertain);
        Assert.Equal(new DateTime(2005, 9, 27), r.DateOfBirth);
        // The Ñ used to make the phone skip the name line and take the address
        // ("NASIPIT, TALAMBAN") as the name - then reject the scan as "Name doesn't match".
        Assert.Equal("MIÑOZA, ALEXA ROSE INSO", r.FullName);
        Assert.True(LtoLicenseParser.NameMatches(Registered, r.FullName));
        Assert.Equal("A, B, B1, B2", r.DlCodes);
        Assert.Equal("G01", r.AgencyCode);
        Assert.True(r.IsPhilippineLicense);
        Assert.True(r.IsConfident);
    }

    [Fact]
    public void Glare_over_the_seal_leaves_the_number_blank_instead_of_guessing()
    {
        // Glare wiped "0096" off the number; every pass read it short or garbled
        // ("GO1-25-0937", "G01-25-Ca937", "G01-25-09837"...). The 5-digit read must not be shown
        // as if it were the number.
        var r = LtoLicenseParser.ParseBest(Passes("mlkit_lic_glare.txt"));

        Assert.Null(r.LicenseNumber);
        Assert.False(r.IsConfident);
        // Everything off the seal still reads.
        Assert.Equal(new DateTime(2030, 9, 27), r.ExpiryDate);
        Assert.True(LtoLicenseParser.NameMatches(Registered, r.FullName));
        Assert.Equal("A, B, B1, B2", r.DlCodes);
        Assert.Equal("Female", r.Sex);
    }

    [Fact]
    public void Retake_at_another_angle_is_merged_with_the_glare_photo()
    {
        // The Scan Driver License page keeps the glare photo's reads and votes them together
        // with the retake's (retake first).
        var merged = Passes("mlkit_lic_glare_right.txt").Concat(Passes("mlkit_lic_glare.txt"));
        var r = LtoLicenseParser.ParseBest(merged);

        Assert.Equal("G01-25-009637", r.LicenseNumber);
        Assert.False(r.LicenseNumberUncertain);
        Assert.Equal(new DateTime(2030, 9, 27), r.ExpiryDate);
    }

    [Theory]
    [InlineData("mlkit_lic_dark.txt")]
    [InlineData("mlkit_lic_blur.txt")]
    [InlineData("mlkit_lic_shake.txt")]
    [InlineData("mlkit_lic_faded.txt")]
    [InlineData("mlkit_lic_shadow.txt")]
    [InlineData("mlkit_lic_combo.txt")]
    [InlineData("mlkit_lic_glare_mid.txt")]
    [InlineData("mlkit_lic_glare_right.txt")]
    public void Poor_photos_still_read(string fixture)
    {
        var r = LtoLicenseParser.ParseBest(Passes(fixture));

        Assert.Equal("G01-25-009637", r.LicenseNumber);
        Assert.False(r.LicenseNumberUncertain);
        Assert.Equal(new DateTime(2030, 9, 27), r.ExpiryDate);
        Assert.True(LtoLicenseParser.NameMatches(Registered, r.FullName));
    }

    [Fact]
    public void Number_read_by_repairing_letters_is_flagged()
    {
        // "G09637": the first 0 under the seal read as G. Turned back into 009637, but shown
        // with "check it against the card".
        var r = LtoLicenseParser.Parse(Card("G01-25-G09637"));

        Assert.Equal("G01-25-009637", r.LicenseNumber);
        Assert.True(r.LicenseNumberUncertain);
        Assert.False(r.IsConfident);
    }

    [Fact]
    public void Passes_that_disagree_are_flagged_and_a_clear_majority_is_not()
    {
        var split = LtoLicenseParser.ParseBest(new[] { Card("G01-25-009637"), Card("G01-25-009687") });
        Assert.True(split.LicenseNumberUncertain);

        var majority = LtoLicenseParser.ParseBest(new[] { Card("G01-25-009637"), Card("G01-25-009637"), Card("G01-25-009687") });
        Assert.Equal("G01-25-009637", majority.LicenseNumber);
        Assert.False(majority.LicenseNumberUncertain);
    }

    [Fact]
    public void Number_not_matching_the_agency_code_is_flagged()
    {
        var r = LtoLicenseParser.Parse(Card("N01-25-009637"));
        Assert.True(r.LicenseNumberUncertain);
    }

    [Fact]
    public void Number_split_over_two_lines_is_joined()
    {
        var r = LtoLicenseParser.Parse(Card("G01-25-\n009637"));
        Assert.Equal("G01-25-009637", r.LicenseNumber);
    }

    [Fact]
    public void Five_digit_specimen_number_is_kept_but_flagged()
    {
        // Sample/specimen cards print a 5-digit tail (B12-19-00375); real cards have 6.
        var r = LtoLicenseParser.Parse(
            "REPUBLIC OF THE PHILIPPINES\nLAND TRANSPORTATION OFFICE\nNON-PROFESSIONAL DRIVER'S LICENSE\n" +
            "Last Name. First Name. Middle Name\nDELA CRUZ, JOSIE\nNationality Sex Date of Birth\nPHL F 2002/07/06\n" +
            "License No. Expiration Date Agency Code\nB12-19-00375 2030/07/06 B12");

        Assert.Equal("B12-19-00375", r.LicenseNumber);
        Assert.True(r.LicenseNumberUncertain);
        Assert.Equal("DELA CRUZ, JOSIE", r.FullName);
        Assert.Equal("Non-Professional", r.Classification);
        Assert.Equal(new DateTime(2030, 7, 6), r.ExpiryDate);
    }

    [Fact]
    public void Address_is_never_taken_as_the_name()
    {
        var r = LtoLicenseParser.Parse(
            "REPUBLIC OF THE PHILIPPINES\nDRIVER'S LICENSE\nAddress\nNASIPIT, TALAMBAN, CEBU CITY (CAPITAL),\nCEBU, 6000\n" +
            "License No.\nG01-25-009637 2030/09/27");
        Assert.Null(r.FullName);
    }

    /// <summary>A plain card read with <paramref name="number"/> on the License No. row.</summary>
    private static string Card(string number) =>
        "REPUBLIC OF THE PHILIPPINES\nDEPARTMENT OF TRANSPORTATION\nLAND TRANSPORTATION OFFICE\nDRIVER'S LICENSE\n" +
        "Last Name. First Name. Middle Name\nMIÑOZA, ALEXA ROSE INSO\nNationality Sex Date of Birth\nPHL F 2005/09/27\n" +
        $"License No.\nExpiration Date\n{number} 2030/09/27\nAgency Code\nG01\nDL Codes\nA,B,B1,B2";
}
