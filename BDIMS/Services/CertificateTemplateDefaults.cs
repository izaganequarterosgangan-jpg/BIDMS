namespace BDIMS.Services
{
    /// Factory HTML shipped with the application for certificate types that do not
    /// yet have an administrator-authored template.
    public static class CertificateTemplateDefaults
    {
        /// <summary>
        /// Official Barangay Indigency layout. Persistent header (seals, office
        /// title, borders) plus {{ Token }} placeholders for resident details.
        /// </summary>
        public const string BarangayIndigencyTemplateHtml = """
            <div class="certificate-container" style="font-family: 'Times New Roman', serif; padding: 40px; color: #000; max-width: 800px; margin: auto; background: #fff;">

              <!-- HEADER WITH LOGOS -->
              <div style="display: flex; align-items: center; justify-content: space-between; border-bottom: 2px solid #000; padding-bottom: 15px; margin-bottom: 30px;">
                <!-- Left Seal: Barangay Governor Boyles -->
                <img src="/images/logos/brgy_governor_boyles_seal.png" alt="Barangay Seal" style="width: 100px; height: 100px; object-fit: contain;" onerror="this.style.display='none';" />

                <!-- Center Text -->
                <div style="text-align: center; flex: 1; padding: 0 10px;">
                  <p style="margin: 0; font-size: 14px;">Republic of the Philippines</p>
                  <p style="margin: 0; font-size: 14px;">Province of {{ Province }}</p>
                  <p style="margin: 0; font-size: 14px;">Municipality of {{ Municipality }}</p>
                  <h3 style="margin: 5px 0 0 0; font-size: 18px; font-weight: bold; text-transform: uppercase;">Barangay {{ BarangayName }}</h3>
                  <h4 style="margin: 2px 0 0 0; font-size: 15px; font-weight: bold; color: #1a365d;">OFFICE OF BARANGAY CAPTAIN</h4>
                </div>

                <!-- Right Seal: Municipality of Ubay -->
                <img src="/images/logos/ubay_municipality_seal.png" alt="Municipality Seal" style="width: 100px; height: 100px; object-fit: contain;" onerror="this.style.display='none';" />
              </div>

              <!-- TITLE -->
              <div style="text-align: center; margin: 40px 0;">
                <h2 style="font-size: 26px; font-weight: bold; letter-spacing: 2px; text-decoration: underline; margin: 0;">CERTIFICATION</h2>
              </div>

              <!-- BODY CONTENT -->
              <div style="font-size: 16px; line-height: 1.8; text-align: justify; margin-bottom: 30px;">
                <p style="margin-bottom: 20px;"><strong>TO WHOM IT MAY CONCERN:</strong></p>

                <p style="text-indent: 50px; margin-bottom: 15px;">
                  This is to certify that <strong>{{ ResidentName }}</strong>, <strong>{{ ResidentAge }}</strong>, a resident of <strong>{{ Purok }}, {{ BarangayName }}, {{ Municipality }}, {{ Province }}</strong> is a bonafide member and resident of this barangay.
                </p>

                <p style="text-indent: 50px; margin-bottom: 15px;">
                  This certifies that he/she has no derogatory record and has a good moral character as per our barangay record concerned.
                </p>

                <p style="text-indent: 50px; margin-bottom: 30px;">
                  This <strong>CERTIFICATION</strong> is being issued upon the request of the above-mentioned name in connection to his/her <strong>{{ Purpose }}</strong> and for whatever legal purposes it may serve.
                </p>

                <p style="text-indent: 50px; margin-bottom: 50px;">
                  Given this <strong>{{ IssueDate }}</strong>, at Barangay {{ BarangayName }}, {{ Municipality }}, {{ Province }}, Philippines.
                </p>
              </div>

              <!-- SIGNATURE SECTION -->
              <div style="display: flex; justify-content: flex-end; margin-top: 60px;">
                <div style="text-align: center; width: 280px;">
                  <p style="margin: 0; font-weight: bold; font-size: 16px; border-bottom: 1px solid #000; padding-bottom: 2px;">{{ BarangayCaptain }}</p>
                  <p style="margin: 5px 0 0 0; font-size: 14px;">{{ SignatoryRole }}</p>
                </div>
              </div>

            </div>
            """;

        /// <summary>
        /// Neutral starting point for a certificate type that has no template yet.
        /// </summary>
        public static string BuildSkeleton(string title, string bodyText)
        {
            // The heading is the certificate type name, which reaches this method straight
            // from a form field or the query string. It is interpolated into markup, so it
            // is HTML-encoded exactly like bodyText below; otherwise a type named
            // "<img src=x onerror=...>" would execute in the preview and print windows.
            var heading = WebUtilityText((title ?? string.Empty).ToUpperInvariant());

            // Four $ means an interpolation hole is a run of four or more braces
            // ($$$$""" ... {{{{expr}}}}} ... """), so the two-brace {{ Token }}
            // placeholders in the body below survive verbatim as literal text. A
            // token must therefore be written with two braces, never four: {{{{ Province }}}}
            // would be read as an interpolation hole naming a variable "Province",
            // which does not compile. The three holes below are the real
            // interpolations - the heading and the already-escaped body text.
            return $$$$"""
                <div class="certificate-container" style="font-family: 'Times New Roman', serif; padding: 40px; color: #000; max-width: 800px; margin: auto; background: #fff;">

                  <!-- HEADER WITH LOGOS -->
                  <div style="display: flex; align-items: center; justify-content: space-between; border-bottom: 2px solid #000; padding-bottom: 15px; margin-bottom: 30px;">
                    <img src="/images/logos/brgy_governor_boyles_seal.png" alt="Barangay Seal" style="width: 100px; height: 100px; object-fit: contain;" onerror="this.style.display='none';" />

                    <div style="text-align: center; flex: 1; padding: 0 10px;">
                      <p style="margin: 0; font-size: 14px;">Republic of the Philippines</p>
                      <p style="margin: 0; font-size: 14px;">Province of {{ Province }}</p>
                      <p style="margin: 0; font-size: 14px;">Municipality of {{ Municipality }}</p>
                      <h3 style="margin: 5px 0 0 0; font-size: 18px; font-weight: bold; text-transform: uppercase;">Barangay {{ BarangayName }}</h3>
                      <h4 style="margin: 2px 0 0 0; font-size: 15px; font-weight: bold; color: #1a365d;">OFFICE OF BARANGAY CAPTAIN</h4>
                    </div>

                    <img src="/images/logos/ubay_municipality_seal.png" alt="Municipality Seal" style="width: 100px; height: 100px; object-fit: contain;" onerror="this.style.display='none';" />
                  </div>

                  <!-- TITLE -->
                  <div style="text-align: center; margin: 40px 0;">
                    <h2 style="font-size: 26px; font-weight: bold; letter-spacing: 2px; text-decoration: underline; margin: 0;">{{{{heading}}}}</h2>
                  </div>

                  <!-- BODY CONTENT -->
                  <div style="font-size: 16px; line-height: 1.8; text-align: justify; margin-bottom: 30px;">
                    <p style="margin-bottom: 20px;"><strong>TO WHOM IT MAY CONCERN:</strong></p>

                    <p style="text-indent: 50px; margin-bottom: 15px;">
                      This is to certify that <strong>{{ ResidentName }}</strong>, <strong>{{ ResidentAge }}</strong>, a resident of <strong>{{ Purok }}, {{ BarangayName }}, {{ Municipality }}, {{ Province }}</strong>, {{{{WebUtilityText(bodyText)}}}}}
                    </p>

                    <p style="text-indent: 50px; margin-bottom: 30px;">
                      This <strong>{{{{heading}}}}</strong> is being issued upon the request of the above-mentioned name in connection to his/her <strong>{{ Purpose }}</strong> and for whatever legal purposes it may serve.
                    </p>

                    <p style="text-indent: 50px; margin-bottom: 50px;">
                      Given this <strong>{{ IssueDate }}</strong>, at Barangay {{ BarangayName }}, {{ Municipality }}, {{ Province }}, Philippines.
                    </p>
                  </div>

                  <!-- SIGNATURE SECTION -->
                  <div style="display: flex; justify-content: flex-end; margin-top: 60px;">
                    <div style="text-align: center; width: 280px;">
                      <p style="margin: 0; font-weight: bold; font-size: 16px; border-bottom: 1px solid #000; padding-bottom: 2px;">{{ BarangayCaptain }}</p>
                      <p style="margin: 5px 0 0 0; font-size: 14px;">{{ SignatoryRole }}</p>
                    </div>
                  </div>

                </div>
                """;
        }

        /// <summary>
        /// Builds a layout on demand for a certificate type that has never been saved in
        /// the Template Editor, so a certificate can always be previewed and printed.
        /// The heading and fee come from the type itself rather than a per-type hardcoded
        /// view, so adding a certificate type needs no code change.
        /// </summary>
        public static string BuildFallbackFor(string? certificateType)
        {
            var title = ShortTitle(certificateType);
            var body = IsIndigency(title)
                ? "declared by the undersigned to be an indigent resident of this barangay, and the "
                  + "undersigned hereby certifies the same for whatever legal purposes it may serve."
                : "a bona fide resident of this barangay, and the undersigned hereby certifies the "
                  + "same for whatever legal purposes it may serve.";

            return BuildSkeleton(title, body);
        }

        /// <summary>
        /// Drops the parenthetical qualifier the fee table uses, e.g.
        /// "Certificate of Indigency (Medical / Educational / Legal Aid)"
        /// becomes "Certificate of Indigency".
        /// </summary>
        public static string ShortTitle(string? certificateType)
        {
            var type = (certificateType ?? string.Empty).Trim();

            if (type.Length == 0)
            {
                return "Certificate";
            }

            var paren = type.IndexOf('(');
            if (paren > 0)
            {
                type = type.Substring(0, paren).Trim();
            }

            return type.Length == 0 ? "Certificate" : type;
        }

        private static bool IsIndigency(string title) =>
            title.Contains("indigency", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("indigent", StringComparison.OrdinalIgnoreCase);

        private static string WebUtilityText(string? value) =>
            System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
    }
}
