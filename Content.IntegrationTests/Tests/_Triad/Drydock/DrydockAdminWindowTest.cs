#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Client._NF.Shipyard.UI;
using Content.Client._Triad.Drydock.Admin;
using Content.Shared._Triad.Drydock.Admin;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Localization;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// The drydock admin panel as it actually draws. These build the real window on a real client
    /// and read the control tree back, which is the only thing short of a screenshot that can say
    /// a surface is what it was designed to be.
    ///
    /// <para>They exist because the panel was twice wrong in ways nothing else could catch: every
    /// state chip lit at once, and a layout that no test disagreed with because no test looked.
    /// Controls are found by their XAML name rather than by making fields public, so the
    /// production surface is unchanged by being tested.</para>
    /// </summary>
    [TestFixture]
    public sealed class DrydockAdminWindowTest
    {
        [Test]
        public async Task ExactlyOneStateChipIsEverLit()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var window = new DrydockAdminWindow(new DrydockAdminEui());
                window.UpdateState(StateWith(Ship("Kestrel", "Stored")));

                var chips = Named(window, "ChipRow").Children.OfType<ContainerButton>().ToList();
                Assert.That(chips, Has.Count.EqualTo(8), "All, the five states, and the two flags.");

                // The chip in force is the one drawn filled. Two filled at once is the bug this
                // catches: it is what a toggle button's own pressed state did before they were
                // drawn by hand.
                var lit = chips.Count(c => IsLit(c));
                Assert.That(lit, Is.EqualTo(1), "Exactly one chip is ever lit, and at rest it is All.");

                window.Dispose();
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// The whole point of the verb row: what a hull offers follows the state it is in, and a
        /// verb that cannot apply is absent rather than greyed.
        /// </summary>
        [Test]
        [TestCase("Stored", new[] { "hold", "investigate" }, new[] { "cancel-offer", "restore-from-sale", "restore-to" })]
        [TestCase("CheckedOut", new[] { "hold", "investigate", "restore-to" }, new[] { "cancel-offer", "restore-from-sale" })]
        [TestCase("Held", new[] { "release", "investigate", "restore-to" }, new[] { "cancel-offer", "restore-from-sale" })]
        [TestCase("Sold", new[] { "restore-from-sale", "hold", "investigate", "restore-to" }, new[] { "cancel-offer" })]
        public async Task TheVerbsFollowTheStateOfTheHull(string state, string[] expected, string[] absent)
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var window = new DrydockAdminWindow(new DrydockAdminEui());
                window.UpdateState(StateWith(Ship("Kestrel", state), sold: state == "Sold"));

                var labels = VerbLabels(window);

                Assert.Multiple(() =>
                {
                    foreach (var key in expected)
                        Assert.That(labels, Does.Contain(Text($"drydock-admin-{key}")), $"{state} offers {key}.");

                    foreach (var key in absent)
                        Assert.That(labels, Does.Not.Contain(Text($"drydock-admin-{key}")), $"{state} does not offer {key}.");
                });

                window.Dispose();
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>An escrow hull leads with the withdrawal, and draws the card that explains it.</summary>
        [Test]
        public async Task AnEscrowHullLeadsWithCancelOfferAndDrawsTheCard()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var window = new DrydockAdminWindow(new DrydockAdminEui());
                window.UpdateState(StateWith(Ship("Kestrel", "InEscrow"), escrow: true));

                Assert.Multiple(() =>
                {
                    Assert.That(VerbLabels(window).First(), Is.EqualTo(Text("drydock-admin-cancel-offer")),
                        "The verb that answers the state comes first.");
                    Assert.That(Named(window, "EscrowPanel").Visible, Is.True);
                });

                // And it is gone again for a hull that is not in escrow.
                window.UpdateState(StateWith(Ship("Kestrel", "Stored")));
                Assert.That(Named(window, "EscrowPanel").Visible, Is.False, "The card does not outlive the offer.");

                window.Dispose();
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task TheListsDrawOneRowPerThingTheyWereGiven()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var window = new DrydockAdminWindow(new DrydockAdminEui());
                var state = StateWith(Ship("Kestrel", "Stored"), Ship("Behir", "CheckedOut"), Ship("Pelican", "Held"));
                window.UpdateState(state);

                Assert.Multiple(() =>
                {
                    Assert.That(Named(window, "ShipContainer").ChildCount, Is.EqualTo(3), "One row per hull that matched.");
                    Assert.That(Named(window, "BerthContainer").Children.OfType<DrydockBerthRow>().Count(),
                        Is.EqualTo(state.OwnerBerths.Count),
                        "The owner's berths, drawn by the player's own row control.");
                    Assert.That(Named(window, "TimelineContainer").ChildCount,
                        Is.EqualTo(state.Selected!.Timeline.Count),
                        "One line per audit entry; a revision is a row here, not a panel of its own.");
                });

                window.Dispose();
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// With nothing picked the right-hand side says so and offers nothing, rather than drawing
        /// empty panels and an editable notes box for a hull that is not there.
        /// </summary>
        [Test]
        public async Task WithNothingSelectedThereIsNothingToActon()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var window = new DrydockAdminWindow(new DrydockAdminEui());
                window.UpdateState(new DrydockAdminEuiState { TotalShips = 0 });

                Assert.Multiple(() =>
                {
                    Assert.That(VerbLabels(window), Is.Empty, "No hull, no verbs.");
                    Assert.That(Named(window, "BerthContainer").ChildCount, Is.Zero);
                    Assert.That(Named(window, "TimelineContainer").ChildCount, Is.Zero);
                    Assert.That(Named(window, "EscrowPanel").Visible, Is.False);
                    Assert.That(((LineEdit)Named(window, "NotesInput")).Editable, Is.False,
                        "Notes belong to a hull, so there is nothing to type into.");
                });

                window.Dispose();
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Nothing the panel draws may come out as a raw Fluent key. This is the check that a
        /// missing label fails loudly instead of shipping as "drydock-admin-action-ShipSold" in
        /// front of an admin.
        /// </summary>
        [Test]
        public async Task NothingIsDrawnAsARawLocaleKey()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });

            await pair.Client.WaitPost(() =>
            {
                var window = new DrydockAdminWindow(new DrydockAdminEui());
                window.UpdateState(StateWith(Ship("Kestrel", "InEscrow"), escrow: true));

                // Rich text has to be read back through GetMessage: the timeline, the header and
                // every row are RichTextLabels, and a scan of plain Labels alone sees none of
                // them. That blindness let a deliberately deleted key pass this test once.
                var drawn = Descendants(window).OfType<Label>().Select(l => l.Text)
                    .Concat(Descendants(window).OfType<Button>().Select(b => b.Text))
                    .Concat(Descendants(window).OfType<RichTextLabel>().Select(r => r.GetMessage()))
                    .Where(t => !string.IsNullOrEmpty(t))
                    .ToList();

                var raw = drawn
                    .Where(t => t!.Contains("drydock-admin-") || t.Contains("shipyard-console-"))
                    .ToList();

                Assert.That(raw, Is.Empty, "A key drawn as text is a key with no entry in the ftl.");

                // Every audit action has a label, including the ones this sample does not happen
                // to contain. The tree scan can only see the rows it was given.
                var unlabelled = Enum.GetNames<Content.Server.Database.DrydockAuditAction>()
                    .Where(a => Text($"drydock-admin-action-{a}") == $"drydock-admin-action-{a}")
                    .ToList();
                Assert.That(unlabelled, Is.Empty, "An action with no label renders as its own key.");

                // Control: an absent key resolves to itself, which is what both checks look for.
                Assert.That(Text("drydock-admin-not-a-real-key"), Is.EqualTo("drydock-admin-not-a-real-key"));

                window.Dispose();
            });

            await pair.CleanReturnAsync();
        }

        // ------------------------------------------------------------------ helpers

        private static string Text(string key) => Loc.GetString(key);

        private static IEnumerable<Control> Descendants(Control root)
        {
            foreach (var child in root.Children)
            {
                yield return child;
                foreach (var deeper in Descendants(child))
                    yield return deeper;
            }
        }

        private static Control Named(Control root, string name)
            => Descendants(root).Single(c => c.Name == name);

        private static List<string> VerbLabels(Control window)
            => Named(window, "VerbRow").Children.OfType<Button>().Select(b => b.Text ?? string.Empty).ToList();

        /// <summary>A chip is lit when its panel is drawn in the accent rather than the resting fill.</summary>
        private static bool IsLit(ContainerButton chip)
        {
            var panel = chip.Children.OfType<PanelContainer>().Single();
            return panel.PanelOverride is Robust.Client.Graphics.StyleBoxFlat box
                   && box.BackgroundColor != Robust.Shared.Maths.Color.FromHex("#222226");
        }

        private static DrydockAdminShipDto Ship(string name, string state) => new(
            Guid.NewGuid(), name, Guid.NewGuid(), "Mara Voss", state, Investigating: false,
            "Cutter", "TestVessel", BerthId: 12, LastBerthId: 12, CheckedOutRoundId: 4112,
            DateTime.UtcNow, CurrentRevision: 7, LiveThisRound: false,
            EscrowExpiresAt: state == "InEscrow" ? DateTime.UtcNow.AddMinutes(27) : null);

        private static DrydockAdminEuiState StateWith(DrydockAdminShipDto selected, params DrydockAdminShipDto[] others)
            => StateWith(selected, false, false, others);

        private static DrydockAdminEuiState StateWith(DrydockAdminShipDto selected, bool escrow = false, bool sold = false, params DrydockAdminShipDto[] others)
        {
            var ships = new List<DrydockAdminShipDto> { selected };
            ships.AddRange(others);

            var timeline = new List<DrydockAdminAuditDto>
            {
                new(1, DateTime.UtcNow, "Store", Guid.NewGuid(), "Mara Voss", null, null, 7, 12, 4112, null, selected.Name),
                new(2, DateTime.UtcNow.AddMinutes(-5), "AccessRefused", Guid.NewGuid(), "Dov Ashkenazi", selected.OwnerUserId, "Mara Voss", null, null, 4112, null, selected.Name),
            };

            return new DrydockAdminEuiState
            {
                Ships = ships,
                TotalShips = ships.Count,
                CurrentRoundId = 4112,
                OwnerBerths =
                {
                    new DrydockAdminBerthDto(12, "Cutter", "Purchased", 2500, selected.ShipGuid, selected.Name, "Cutter", selected.State),
                    new DrydockAdminBerthDto(15, "Frigate", "Purchased", 10000, null, null, null, null),
                },
                Selected = new DrydockAdminShipDetailDto(
                    selected,
                    "ticket #91",
                    new List<DrydockAdminRevisionDto>
                    {
                        new(7, "PlayerStore", DateTime.UtcNow, 4112, null, null, 2048, true, null, 24000),
                    },
                    timeline,
                    escrow
                        ? new DrydockAdminEscrowDto(1, selected.OwnerUserId, "Mara Voss", Guid.NewGuid(), "Tomas Reyes",
                            DateTime.UtcNow, DateTime.UtcNow.AddMinutes(27), 40)
                        : null,
                    sold ? new DrydockAdminSaleDto(8400, DateTime.UtcNow, 12300) : null),
            };
        }
    }
}
