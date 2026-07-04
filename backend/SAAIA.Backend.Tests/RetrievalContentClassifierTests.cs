using Xunit;

namespace SAAIA.Backend.Tests;

public sealed class RetrievalContentClassifierTests
{
    [Theory]
    [InlineData("Sommaire Installation 3 Configuration 8 Maintenance 12 Annexes 18")]
    [InlineData("Table of contents Safety overview 3 Lockout procedure 8 Alarm reset 12 Maintenance plan 18")]
    [InlineData("Índice Seguridad 3 Procedimiento de bloqueo 8 Mantenimiento 12 Anexos 18")]
    [InlineData("Sumário Segurança 3 Procedimento de bloqueio 8 Manutenção 12 Anexos 18")]
    [InlineData("Sommario Sicurezza 3 Procedura di blocco 8 Manutenzione 12 Allegati 18")]
    [InlineData("Inhaltsverzeichnis Sicherheit 3 Verriegelungsverfahren 8 Wartung 12 Anhänge 18")]
    [InlineData("IndexA, BMotor startup 18Pressure calibration 22Valve inspection 24Weekly checklist 31Yearly shutdown 44Alarm acknowledgement 48Backup restore 52Control cabinet 57Drive replacement 64Emergency stop 72Filter exchange 81Hydraulic test 93Inspection checklist 104")]
    public void DetectNavigationReason_marks_toc_and_compact_indexes(string text)
    {
        var reason = RetrievalContentClassifier.DetectNavigationReason(text);

        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("Sommaire")]
    [InlineData("Contents")]
    [InlineData("TOC")]
    public void AnalyzeChunk_marks_standalone_toc_marker_as_navigation(string text)
    {
        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.NavigationRole, signal.ContentRole);
        Assert.Equal("table_of_contents", signal.NavigationReason);
    }

    [Fact]
    public void DetectNavigationReason_does_not_treat_content_indice_as_navigation()
    {
        var text = "Indice de viscosité: la méthode explique comment mesurer la variation du fluide, comparer les seuils et consigner le résultat.";

        var reason = RetrievalContentClassifier.DetectNavigationReason(text);

        Assert.Null(reason);
    }

    [Fact]
    public void AnalyzeChunk_keeps_dense_technical_index_term_as_content()
    {
        var text = "The revision index identifies the revision status of the document. Different versions are numbered in consecutive order by means of, e.g. a letter or letter combination A to Z, then AA, AB, AC ... or Figures 1, 2, 3 ... The letters I and O should be avoided because they are easily confused with the digits 1 and 0. Alternatively, the date of issue field only may be used.";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
    }

    [Fact]
    public void AnalyzeChunk_scores_dot_leader_toc_as_navigation()
    {
        var text = """
Safety overview .................... 3
Lockout procedure .................. 8
Alarm reset ........................ 12
Maintenance plan ................... 18
Appendix ........................... 24
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.NavigationRole, signal.ContentRole);
        Assert.True(signal.NavigationScore >= 0.80);
        Assert.NotNull(signal.NavigationReason);
    }

    [Fact]
    public void AnalyzeChunk_marks_single_noisy_dot_leader_page_reference_as_navigation()
    {
        var text = "16 Drop tests and impact tests................. iii 37 a INN een 37 .";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.NavigationRole, signal.ContentRole);
        Assert.Equal("single_title_page_reference", signal.NavigationReason);
        Assert.True(signal.NavigationScore >= 0.80);
    }

    [Fact]
    public void AnalyzeChunk_marks_title_catalog_with_real_body_as_mixed()
    {
        var text = """
Controls overview 3
Maintenance plan 18
Alarm reset 22
Lockout checklist 27
Appendix 31
Procedure body: Materials lock padlock warning tag. Procedure 1. Isolate the machine. 2. Verify zero energy and document the result.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.MixedNavigationContentRole, signal.ContentRole);
        Assert.True(signal.NavigationScore >= 0.55);
        Assert.True(signal.ContentDensityScore >= 0.50);
    }

    [Fact]
    public void AnalyzeChunk_keeps_dense_measured_instructional_content_as_content()
    {
        var text = """
Batch preparation for 4 units • 1.2 kg base compound • 250 g additive • 100 g binder • 2 modules • 3 cm spacer • 25 min curing time • 180° C oven.
Preparation: warm the base, mix the additive, place the spacer, check the control value and document the result before packaging.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
        Assert.True(signal.ContentDensityScore >= 0.70);
    }

    [Fact]
    public void AnalyzeChunk_keeps_dense_ocr_joined_instructional_content_as_content()
    {
        var text = """
Preparation : 20 minutes Control : 180° C For 4 units Preparation e 1kgde base e 250gadditive e 25clsolution.
Procedure e Warm the base and verify the control point. e Add the solution slowly. e Document the result and package the batch.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
    }

    [Fact]
    public void AnalyzeChunk_keeps_dense_structured_quantity_block_as_content_despite_inline_numbers()
    {
        var text = """
P PREPARATION 1 INGREDIENTS: 400 g base compound 2 modules 100 ml solution 25 min curing time 3 cm spacer 5 s hold 180 C control temperature.
PREPARATION 1. Rinse the modules and dry them. 2. Mix the base compound with the solution. 3. Heat the batch, verify the control value and document the result.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
        Assert.True(signal.ContentDensityScore >= 0.70);
    }

    [Fact]
    public void AnalyzeChunk_keeps_measured_sequential_body_as_content_despite_inline_numbers()
    {
        var text = """
ALPHA BETA MODULE
1 Mix the base with 150 g powder and 20 g binder for 12 min until the control value is stable.
2 Heat the carrier to 85 C for 12 min and record the pressure value before continuing.
3 Cut the inserts into equal pieces, add 50 cl carrier and keep the assembly moving for 20 s.
4 Place each insert in the fixture and hold it for 25 min while the surface cools.
5 Finish the assembly with 18 cl solution, verify the result and document the batch.
4 units 23 min 12 min 25 min.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
        Assert.True(signal.ContentDensityScore >= 0.70);
    }

    [Fact]
    public void DetectNavigationReason_keeps_structured_content_with_pdf_index_artifact()
    {
        var text = "16 Maintenance lockout [Index: ] ASSET-042 Materials padlock warning tag. Procedure 1. Isolate machine. 2. Verify zero energy.";

        var reason = RetrievalContentClassifier.DetectNavigationReason(text);

        Assert.Null(reason);
    }

    [Fact]
    public void AnalyzeChunk_keeps_contact_directory_blocks_as_content()
    {
        var text = """
Switzerland support contact
Zurich Service Center, Hardstrasse 12, 8005 Zurich, phone +41 44 555 10 10, email support-ch@example.com
Geneva Emergency Desk, Rue du Rhone 30, 1204 Geneva, phone +41 22 555 20 20, email support-ge@example.com
Basel Spare Parts Office, Aeschenplatz 4, 4052 Basel, phone +41 61 555 30 30, email parts-bs@example.com
Use these contacts only after isolating the equipment and recording the alarm code.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
    }

    [Fact]
    public void AnalyzeChunk_keeps_compact_contact_channel_blocks_as_content()
    {
        var text = """
APPENDIX I - Contacts Whistleblowing channels: i. Speak Up channel EDP: https://edp.com/en/about-us/speak-up ii. Canal Speak up channel EDPR: https://edpr-investors.com/en/who-we-are/speak-up iii. Ethics channel: https://www.example.com/confidential Contact channel with the Data Protection Officer: privacy@example.com
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
    }

    [Fact]
    public void AnalyzeChunk_keeps_technical_standards_foreword_prose_as_content_despite_many_numbers()
    {
        var text = """
ANSI Z535.6-2006 In 1979, the ANSI Z53 Committee on Safety Colors was combined with the ANSI Z35 Committee on Safety Signs to form the ANSI Z535 Committee on Safety Signs and Colors. The committee develops standards for the design, application, and use of signs, colors and symbols intended to identify and warn against specific hazards. Five subcommittees updated the standards: ANSI Z535.1, Safety Color Code; ANSI Z535.2, Environmental and Facility Safety Signs; ANSI Z535.3, Criteria for Safety Symbols; ANSI Z535.4, Product Safety Signs and Labels; and ANSI Z535.5, Accident Prevention Tags. The 1998 edition was revised to produce the 2002 edition, and the purpose of the new subcommittee was to complement the existing standards with requirements for collateral materials.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
        Assert.True(signal.ContentDensityScore >= 0.60);
    }

    [Fact]
    public void AnalyzeChunk_keeps_german_technical_body_as_content_despite_inline_reference_numbers()
    {
        var text = """
Seite 17 zu DVS 2205 Teil 4 Der Zugversuch wird in Anlehnung an DIN 53 455 durchgefuehrt. Die Verbindungsstelle liegt in der Mitte der Messstrecke. Schweissverbindungen werden entsprechend der tatsaechlichen Ausfuehrung geprueft. Fuer die Abmessungen der Probekorper enthaelt Tabelle 4 Angaben. Zwischen der Dicke h der Probekorper und ihrer Laenge L sowie der Stuetze L sollen folgende Beziehungen bestehen: L = 20 h und L1 = 16 h.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
        Assert.True(signal.ContentDensityScore >= 0.60);
    }

    [Fact]
    public void AnalyzeChunk_keeps_german_welding_design_principles_as_content()
    {
        var text = """
Allgemeine schweisstechnische Gestaltungsgrundsaetze Werden tragende Naehte durch nicht zu vermeidende Teile verdeckt, so ist entweder die Naht vor dem Anschweissen des Teiles zu pruefen oder die Teile sind so zu gestalten. Die Schweissnaehte sind so zu dimensionieren, dass eine Pruefung moeglich ist. DVS, Technischer Ausschuss, Arbeitsgruppe 22 Schweissen und Verarbeiten von Kunststoffhalbfabrikaten. Zu beziehen durch: Deutscher Verlag fuer Schweisstechnik GmbH, 415 Krefeld.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
    }

    [Fact]
    public void AnalyzeChunk_keeps_german_nozzle_and_block_flange_note_as_content()
    {
        var text = """
Stehende Behaelter mit abgestuften Wanddicken rechnerischen Nachweis beachten. Stutzen und Blockflansche Behaelter bei nur einseitiger Zuganglichkeit ist nicht anwendbar, wenn eindringende Fluessigkeit zur Belastung des Spaltes fuehrt.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
    }

    [Fact]
    public void AnalyzeChunk_keeps_ocr_german_vessel_application_table_fragment_as_content()
    {
        var text = """
ON tehende und liegende Behaelter bei beidseitiger Zuganglichkeit Anwendung: Stehende Behaelter mit kegeligen Boden Stehende und liegende Behaelter mit gewoelbten a) bei beidseitiger Stehende Behaelter Rechnerischen Nachweis
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
    }

    [Fact]
    public void AnalyzeChunk_keeps_german_din_welding_bead_reference_as_content()
    {
        var text = """
DIN 16927 Teil 1 wenn die in Tabelle 2 angegebenen Mindestwerte erreicht werden und dabei die so gereckte Schweissraupe nicht reisst, wenn eine Hart-PVC erhoeht schlagzaeh DIN 16 927 Teil 2 weitere Schweissraupe unmittelbar daneben geschweisst wird.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
    }

    [Fact]
    public void AnalyzeChunk_keeps_ocr_german_din_welding_bead_reference_as_content()
    {
        var text = """
DIN 16927 Teil 1 wenn die in Tabelle 2 angegebenen Mindestwerie erreicht wer- DIN 8061 Teil 1 den und dabei die so gereckte SchweiBraupe nicht reisst, wenn eine Hart-PVC, erhoeht schlagzah DIN 16 927 Teil 2 weitere Schweissraupe unmitlelbar daneben geschweisst wird.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
    }

    [Fact]
    public void AnalyzeChunk_keeps_technical_reference_catalog_body_as_content()
    {
        var text = """
DIN 16 933 Die wichtigsten in diesem Zusammenhang interessierenden Normen und Richtlinien sind nachfolgend aufgefuehrt: DIN 16 934 Rohre und Tafeln aus PE weich und PE hart: chemische Bestaendigkeit - Richtlinien DIN 1919 Teil 3 Schweissen von Kunststoffen: Verfahren DIN 7724 Klassifizierung und Begriffsbestimmungen hochpolymerer Werkstoffe aufgrund ihres mechanischen Verhaltens DIN 16 955 Tafeln aus SB-Formmassen: Technische Lieferbedingungen DIN 16 956 Pruefung von Kunststoffen und Bestimmung ihrer Eigenschaften.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
    }

    [Fact]
    public void AnalyzeChunk_keeps_ocr_german_material_standard_catalog_as_content()
    {
        var text = """
DIN 16 963 Rohre aus PB (Polybuten 1): Allgemeine Guete- Probekorpern und Bestimmung ihrer Eigenschafanforderungen - Pruefung DIN 19531 Rohre und Formstuecke aus PVC hart (Polyvinyl- DIN 7749 Kunststoff-Formmassen: Weichmacherhaltige Polychlorid hart) fuer Abwasserleitungen innerhalb von
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
    }

    [Fact]
    public void AnalyzeChunk_keeps_technical_instruction_body_as_content_despite_minor_dot_artifact()
    {
        var text = """
BS 4825-3:1991 Fasten the base plate to the bench. Put a clamp block of the required size on to the base plate with the location for the split cutting ring towards the front. If long lengths of tubes are to be used, an additional support is required to keep the tube level. E.3.2 Cutting the tube NOTE When assembling two expanded type liners on a tube, the tube length required is the distance between their mating liners less 4 mm, i.e. twice the compression thickness of gasket. K..3.2.1 Mark the tube to the length required. Push the tube through the centre hole of the clamp block. Fit the split cutting ring in the clamp block and place the cutting mark on the tube.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
    }

    [Fact]
    public void AnalyzeChunk_keeps_risk_matrix_explanation_as_content_despite_title_catalog_shape()
    {
        var text = """
The following matrices show the signal words, colors, and presence or absence of a safety alert symbol that are assigned for each combination of accident probability, worst credible harm, and probability of worst credible harm. If Worst Credible Severity of Harm is Death or Serious Injury Probability of Accident if Hazardous Situation is not Avoided Probability of Death or Serious Injury if Accident Occurs A WARNING If Worst Credible Severity of Harm is Moderate or Minor Injury For all probabilities: CAUTION If Worst Credible Severity of Harm is Property Damage.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
        Assert.True(signal.ContentDensityScore >= 0.60);
    }

    [Fact]
    public void AnalyzeChunk_keeps_short_questionnaire_example_as_content()
    {
        var text = """
Figure B4 continued Sample Symbol Test Administration Instructions and Booklet Example of a good answer Context: This symbol appears on appliances and machines used in the home and workplace.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
        Assert.True(signal.ContentDensityScore >= 0.60);
    }

    [Fact]
    public void AnalyzeChunk_keeps_compact_technical_topic_list_as_content()
    {
        var text = """
Safety alert symbols Examples of a signal word panel Supplemental directive with safety alert symbol METTETE a Examples of section safety message with signal word panel Examples of section safety message with safety alert symbol Examples of embedded safety message with signal word Embedded safety message with safety alert symbol Providing Information About Safety Messages in Collateral Materials and Product Safety
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
        Assert.True(signal.ContentDensityScore >= 0.55);
    }

    [Fact]
    public void AnalyzeChunk_keeps_technical_diagram_label_block_as_content()
    {
        var text = """
11001LS Dirty hyd filter 11002FLT x2 #XX AWG 11003LT 11003LS 11004LT 11004LS Master stop Transformer disconnect #1 11005LT 11005PS 11808 power on relay 16LT 11006PB Reset Power on 11007PB Manual
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
        Assert.True(signal.ContentDensityScore >= 0.55);
    }

    [Fact]
    public void AnalyzeChunk_keeps_technical_component_legend_without_sentences_as_content()
    {
        var text = """
Gasket aging and oil resistance Control Relay, Unlatch Corrosion resistance indoor and outdoor electrical enclosures Cam Switch Selector Switch Selector Switch Illuminated Door and cover latching requirements
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
        Assert.True(signal.ContentDensityScore >= 0.55);
    }

    [Fact]
    public void AnalyzeChunk_keeps_safety_message_figure_legend_as_content()
    {
        var text = """
Figure B3 Word Message With Hazard Description crush and cut Avoidance Statement Keep out during Type of Hazard Statement burn or cause Consequence Statement Figure B4 Word Message With Hazard Description
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
        Assert.True(signal.ContentDensityScore >= 0.55);
    }

    [Fact]
    public void AnalyzeChunk_keeps_color_tolerance_figure_legend_as_content()
    {
        var text = """
Data found in Table 1. Chroma w = Corner Points of Acceptable Color Tolerance Region = Color Tolerance Chart Colors Figure A1 - Enlarged view of CIE 1931 chromaticity diagram showing the areas representing the Color Tolerance Area for ANSI Z535.1 Safety Yellow
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
        Assert.True(signal.ContentDensityScore >= 0.55);
    }

    [Fact]
    public void AnalyzeChunk_keeps_standard_definition_parameter_block_as_content()
    {
        var text = """
Examples of make/break components are relays, circuit breakers, servo potentiometers, adjustable resistors, switches, connectors, and motor brushes. Installation codes refer to make-and-break and sliding contacts. 3.13 MAXIMUM EXTERNAL CAPACITANCE (C, or C,) — maximum value of capacitance in a circuit that can be connected to the connection facilities of the associated nonincendive field wiring apparatus. 3.14 MAXIMUM EXTERNAL INDUCTANCE (L, or L,) — maximum value of inductance in a circuit that can be connected to the connection facilities. 3.16 MAXIMUM INPUT CURRENT (I; or Imax) — maximum current that can be applied. 3.17 MAXIMUM INPUT POWER (P;) — maximum power in an external circuit.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
        Assert.True(signal.ContentDensityScore >= 0.60);
    }

    [Fact]
    public void AnalyzeChunk_keeps_technical_correlation_table_heading_as_content()
    {
        var text = """
Correlation between Groups for Zone and Groups for Divisions Class I, Division 2 Group Class I, Zone 2 Groups Note IIB plus H2 is not a Group as defined by the NEC Class II, Division 2 Groups Zone 22 Groups Class III
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
        Assert.True(signal.ContentDensityScore >= 0.55);
    }

    [Fact]
    public void AnalyzeChunk_keeps_translation_imprint_contact_notice_as_content()
    {
        var text = """
Technical Help to Exporters has taken all reasonable measures to ensure the accuracy of this translation but regrets that no responsibility can be accepted for any error, omission or inaccuracy. In cases of doubt or dispute, the original language text only is valid. Technical Help to Exporters British Standards Institution Tel: Milton Keynes 0908 220022 Telex: 825777
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
    }

    [Fact]
    public void AnalyzeChunk_keeps_standard_revision_notice_with_working_pressure_as_content()
    {
        var text = """
Jan 1957 Edition 2 Standards Institution Pipe connections for the food industry Original language version: Rorkopplingar for livsmedelsindustrin SMS, Mechanical Engineering Division of the Swedish Standards Institution Supersedes SMS 1145 of December 1947. Title unchanged. New sizes have been included. Thread Rd 60-6 replaces Rd 58-6. Maximum working pressure: SMS 1146, Reg. 79 Reg.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
        Assert.True(signal.ContentDensityScore >= 0.60);
    }

    [Fact]
    public void AnalyzeChunk_keeps_standard_cover_front_matter_as_content()
    {
        var text = """
BS 4825-3 Stainless steel tubes and fittings for the food industry and other hygienic applications - Part 8: Specification for clamp type NO COPYING WITHOUT BSI PERMISSION EXCEPT AS PERMITTED BY COPYRIGHT LAW
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
        Assert.True(signal.ContentDensityScore >= 0.55);
    }

    [Fact]
    public void AnalyzeChunk_keeps_standard_committee_front_matter_as_content()
    {
        var text = """
Water Services Association of England and Wales The following bodies were also represented in the drafting of the standard through subcommittees and panels: Brewers Society Dairy Trade Federation Institution of Production Engineers Milking Machine Manufacturers Association This British Standard, having been prepared under the direction of the Piping Systems Components Standards Policy Committee, was published under the authority of the Standards Board and comes into effect on Amendments issued since publication
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
        Assert.True(signal.ContentDensityScore >= 0.55);
    }

    [Fact]
    public void AnalyzeChunk_keeps_revision_schedule_body_as_content_despite_dates()
    {
        var text = """
The Committee developed the following tentative schedule: All proposed changes are due: June 30, 2009 Revisions will be finalized for letter balloting: April 15, 2010 Letter balloting will be completed by: July 15, 2010 Public reviews will be completed by: March 1, 2011 Drafts will be ready to submit to the publisher: May 31, 2011 December 15, 2011 All proposed changes must be submitted by June 30, 2009. Any proposals received after that date will be deferred to subsequent revisions.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
        Assert.True(signal.ContentDensityScore >= 0.60);
    }

    [Fact]
    public void AnalyzeChunk_keeps_numbered_standard_marking_clause_as_content()
    {
        var text = """
SB5.1.1 The nameplate rating and short circuit current rating shall be marked on the industrial control panel. Exception: An enclosure door label is permitted when the field wiring terminals, fuses, and overload devices remain accessible after installation.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
    }

    [Fact]
    public void AnalyzeChunk_keeps_numbered_standoff_bus_bar_clause_as_content()
    {
        var text = """
D3.3.4 A standoff insulator shall be secured to the enclosure with a steel washer and bolt. The spacing between the bus bar and grounded metal parts shall maintain the required clearance for the rated voltage and short circuit current.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
    }

    [Fact]
    public void AnalyzeChunk_keeps_standard_compliance_reference_fragment_as_content()
    {
        var text = """
Equipment for electric spas, equipment assemblies, and associated equipment shall comply with the Standard for Electric Spas, Equipment Assemblies, and Associated Equipment, UL 1563, Supplement SA, when the industrial control panel supplies field wiring terminals and required markings.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
    }

    [Fact]
    public void AnalyzeChunk_keeps_three_phase_grounding_legend_as_content()
    {
        var text = """
Figure SB4.1 Three-phase, 4-wire supply system. Grounded service conductor, grounding electrode conductor, neutral load conductor, phase conductor, branch circuit protection, disconnect switch, and equipment grounding conductor are identified for the control panel field wiring diagram.
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
    }

    [Fact]
    public void AnalyzeChunk_keeps_compact_electrical_grounding_legend_as_content()
    {
        var text = """
UL 508A JULY 28, 2022 Three-phase, 4-wire with load neutral connection factory bonded neutral A - Grounded service conductor B - Grounding electrode conductor N - Neutral load conductor Three-phase, 4-wire with load neutral connection insulated neutral A - Grounded service conductor B - Grounding electrode conductor
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.Null(signal.NavigationReason);
        Assert.True(signal.ContentDensityScore >= 0.55);
    }

    [Fact]
    public void AnalyzeChunk_marks_compact_technical_clause_catalog_as_navigation()
    {
        var text = """
SB3.1 Internal wiring connections SB3.2 Overcurrent protection of control circuit SB4.1 Short circuit current rating SB4.2 Short circuit current ratings of individual power circuit components SB4.3 Feeder components that limit the short circuit current available SB4.4 Determination of the overall short circuit current rating of the panel SB5.2 Cautionary markings
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.NavigationRole, signal.ContentRole);
        Assert.Equal("technical_clause_title_catalog", signal.NavigationReason);
    }

    [Fact]
    public void AnalyzeChunk_does_not_mark_clause_reference_table_rows_as_navigation()
    {
        var text = """
UL 508A Maximum rating of motor branch circuit device percent of full load current rating Nominal rating of motor branch circuit protective Type of Branch Circuit device, percent of full load Protective Device Ampere Rating Nontime delay fuse See 31.3.7, 31.3.8, 31.3.9(a) See 31.3.7, 31.3.8, 31.3.9(b) Dual element fuse time delay Class CC Class CC Dual element fuse Inverse-time circuit breaker See 31.3.7, 31.3.8, 31.3.9(d)
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.NotEqual(RetrievalContentClassifier.NavigationRole, signal.ContentRole);
        Assert.NotEqual("technical_clause_title_catalog", signal.NavigationReason);
    }

    [Fact]
    public void AnalyzeChunk_keeps_compact_technical_scope_catalog_as_navigation()
    {
        var text = """
ANSI Z535.2-2007 Scope and Purpose Application and Exceptions Safety sign colors and formats Signs for safety instruction or safety equipment location Fire safety signs Directional arrow signs Sign classification selection Three panel signs Two panel signs Application of sign formats by hazard classification Sign color specifications Safety Symbols
""";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.NotEqual(RetrievalContentClassifier.ContentRole, signal.ContentRole);
        Assert.NotNull(signal.NavigationReason);
    }

    [Fact]
    public void AnalyzeChunk_keeps_contact_titles_without_locators_as_navigation()
    {
        var text = "Contents Introduction 3 Contacts 21 Support process 24 Appendix 31";

        var signal = RetrievalContentClassifier.AnalyzeChunk(text);

        Assert.Equal(RetrievalContentClassifier.NavigationRole, signal.ContentRole);
        Assert.NotNull(signal.NavigationReason);
    }

    [Fact]
    public void ClassifyChunk_preserves_original_type_for_navigation_chunks()
    {
        var classification = RetrievalContentClassifier.ClassifyChunk(
            "Table of contents Safety overview 3 Lockout procedure 8 Alarm reset 12 Maintenance plan 18",
            "unit_exact_v1");

        Assert.Equal(RetrievalContentClassifier.NavigationRole, classification.ContentRole);
        Assert.Equal(RetrievalContentClassifier.NavigationChunkType, classification.ChunkType);
        Assert.Equal("unit_exact_v1", classification.OriginalChunkType);
        Assert.True(classification.NavigationScore >= 0.70);
    }
}
