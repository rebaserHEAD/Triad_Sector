# Triad: the drydock admin panel (drydockadmin). One key per control; the tooltips are the only
# tooltips in the drydock, the player tab has none.

drydock-admin-title = Drydock Administration
drydock-admin-flavor-left = SKR-OS Drydock Administration
# The button on the Admin menu's Admin tab that opens the panel.
drydock-admin-button = Drydock

drydock-admin-search-placeholder = Player, ship (any name it has had), ship id, or account id
drydock-admin-search-tooltip = One box. A player name, a ship name including any past name from its timeline, a ship id, or an account id.
drydock-admin-search-button = Search

drydock-admin-chip-All = All
drydock-admin-chip-Stored = Stored
drydock-admin-chip-CheckedOut = Out
drydock-admin-chip-InEscrow = Escrow
drydock-admin-chip-Impounded = Impounded
drydock-admin-chip-Sold = Sold
drydock-admin-chip-Abandoned = Abandoned
drydock-admin-chip-Destroyed = Written off
drydock-admin-chip-Stranded = Stranded
drydock-admin-chip-Stranded-tooltip = Checked out in a round that is over. Each one is a decision.

drydock-admin-count = {$total} match
drydock-admin-page = Page {$page} / {$pages}
drydock-admin-prev = Prev
drydock-admin-next = Next

drydock-admin-row-owner = owner {$owner}
drydock-admin-row-berth = #{$berth}
drydock-admin-row-live = live now
drydock-admin-row-out = out · round {$round}
drydock-admin-row-escrow = escrow · {$left}
drydock-admin-row-impounded = impounded · {$fee}
drydock-admin-row-impounded-locked = impounded · {$fee} · locked

drydock-admin-no-selection = Select a ship.
drydock-admin-header = {$class} · id {$id} · revision {$revision} · {$owner} (account {$account})
drydock-admin-header-berth = Berth {$berth}
drydock-admin-header-no-berth = no berth
drydock-admin-header-since = since {$since}
drydock-admin-header-live = LIVE THIS ROUND

drydock-admin-cancel-offer = Cancel offer
drydock-admin-cancel-offer-tooltip = Withdraw the standing offer on this ship. It returns to Stored in its own berth and the recipient's alert goes.
drydock-admin-restore-from-sale = Restore from sale…
drydock-admin-restore-from-sale-tooltip = Undo the sale: the ship returns to a berth, and by default the price is taken back from the owner.
drydock-admin-impound = Impound…
drydock-admin-impound-tooltip = Takes the hull to the impound lot, from its berth or from the world. The fee, the reason and whether the owner may reclaim it are set in the dialog.
drydock-admin-release = Release
drydock-admin-release-tooltip = Lifts the impound for nothing, into the hull's last berth if it is free, else the smallest free berth that fits. Restore to… picks the berth instead.
drydock-admin-restore-to = Restore to…
drydock-admin-restore-to-tooltip = Puts a ship back in one of the owner's berths. Refused while a live grid still carries it.
drydock-admin-more = ···
drydock-admin-vacate = Vacate berth
drydock-admin-vacate-stored = a stored ship lives in its berth
drydock-admin-vacate-escrow = an escrow ship keeps its berth
drydock-admin-delete-ship = Delete ship record
drydock-admin-delete-ship-tooltip = Removes the ship and its documents. The timeline is kept. Refused while a live grid carries it.
drydock-admin-reason-placeholder = Reason, for the timeline
drydock-admin-reason-tooltip = Written on the timeline row of the next action. Required when leaving money with the owner on a sale reversal.
drydock-admin-notes-placeholder = Notes
drydock-admin-save-notes = Save

drydock-admin-escrow-title = In escrow
drydock-admin-escrow-body = Offered to {$to} (account {$toAccount}) at {$made}.
drydock-admin-escrow-lands = Lands in their Berth {$berth}
drydock-admin-escrow-no-room = No berth of theirs fits it now

drydock-admin-berths-title = {$owner}'s berths · {$free} of {$total} free
drydock-admin-grant-berth = Grant berth
drydock-admin-grant-berth-tooltip = Add a berth of this class to the owner's drydock at no charge. A granted berth refunds nothing if sold.
drydock-admin-berth-delete = Delete berth
drydock-admin-berth-delete-tooltip = Remove an empty berth. A berth with a ship in it is refused; move the ship first.
drydock-admin-berth-restore-here = Restore here
drydock-admin-berth-move-here = Move here
drydock-admin-berth-occupied = occupied
drydock-admin-berth-too-small = too small

drydock-admin-revisions-title = Revisions
drydock-admin-revision = r{$revision} · {$kind} · {$at} · by {$by} · {$size} KB · {$document}
drydock-admin-revision-kept = kept
drydock-admin-revision-pruned = pruned
drydock-admin-revision-appraisal = appraisal {$value}
drydock-admin-promote = Promote revision {$revision}

drydock-admin-timeline-title = Timeline
drydock-admin-refused-tooltip = A message the console never offers: the sending account did not own the ship or berth. Only a modified client sends one. Actor is who sent it, subject is whose ship it was.

drydock-admin-sale-title = Restore {$ship} from sale
drydock-admin-sale-body = Sold for {$price} on {$at}.
drydock-admin-sale-take-back = Take the {$price} back
drydock-admin-sale-take-back-tooltip = Withdraws the sale price from the owner's selected character. Unticks itself when their balance cannot cover it; then a reason is required.
drydock-admin-sale-balance = Owner's balance: {$balance}
drydock-admin-sale-balance-unknown = Owner's balance could not be read.
drydock-admin-sale-berth = Berth
drydock-admin-sale-reason-placeholder = Reason (required when the money stays)
drydock-admin-sale-confirm = Restore
drydock-admin-sale-cancel = Cancel

# The impound dialog. The fee is a share of the appraisal; the credits beside the slider are what
# that share comes to, computed the way the server computes it.
drydock-admin-impound-title = Impound {$ship}
drydock-admin-impound-body = {$ship} goes to the impound lot. It holds no berth there.
drydock-admin-impound-fee = Fee
drydock-admin-impound-fee-note = of the {$appraisal} appraisal. 0% is free, 100% is the whole hull.
drydock-admin-impound-fee-note-live = of the {$appraisal} it last appraised at; a hull still in the world is appraised again as it is taken.
drydock-admin-impound-fee-note-unknown = No appraisal is on file for this ship, so any share comes to nothing.
drydock-admin-impound-reason = Reason
drydock-admin-impound-reason-placeholder = What the hull was taken for
drydock-admin-impound-reason-note = Shown to the owner.
drydock-admin-impound-redeemable = Owner can reclaim
drydock-admin-impound-redeemable-detail = · or abandon it instead
drydock-admin-impound-redeemable-note = Untick to hold the hull through an adjudication.
drydock-admin-impound-confirm = Impound
drydock-admin-impound-cancel = Cancel

drydock-admin-minutes-left = {$minutes} min left
drydock-admin-expired = expired


# Added when the panel was rebuilt to the canvas: the escrow card's second line, the two
# section hints, the empty-selection berth heading, and one label per audit action so the
# timeline reads as prose instead of enum names.
drydock-admin-escrow-expiry = Expires {$expires}, {$left}. The ship keeps its berth and refuses retrieve, sell, rename and move until then.
drydock-admin-berths-title-empty = Berths
drydock-admin-berths-hint = Same rows the player sees on the console.
drydock-admin-timeline-hint = Every row is one audit entry. Actor first, then who it was done to.
drydock-admin-notes-title = Notes

drydock-admin-action-Store = Stored
drydock-admin-action-Retrieve = Retrieved
drydock-admin-action-Restore = Restored
drydock-admin-action-Transfer = Transferred
drydock-admin-action-Delete = Deleted
drydock-admin-action-Rebake = Re-baked
drydock-admin-action-Impound = Impounded
drydock-admin-action-ImpoundReleased = Released
drydock-admin-action-BerthPurchase = Berth purchased
drydock-admin-action-BerthSale = Berth sold
drydock-admin-action-BerthGrant = Berth granted
drydock-admin-action-BerthUpgrade = Berth upgraded
drydock-admin-action-BerthMove = Moved berth
drydock-admin-action-BerthDelete = Berth deleted
drydock-admin-action-Fallback = Fell back to an older revision
drydock-admin-action-AccessRefused = Access refused
drydock-admin-action-TransferOffered = Transfer offered
drydock-admin-action-TransferDeclined = Transfer declined
drydock-admin-action-TransferCancelled = Transfer withdrawn
drydock-admin-action-TransferExpired = Transfer expired
drydock-admin-action-ShipSold = Sold
drydock-admin-action-Renamed = Renamed
drydock-admin-action-SaleReversed = Sale reversed
drydock-admin-action-Imported = Imported
drydock-admin-action-ImpoundRedeemed = Reclaimed
drydock-admin-action-ShipAbandoned = Abandoned
drydock-admin-action-AbandonReversed = Abandon reversed
drydock-admin-action-ShipDestroyed = Written off
drydock-admin-action-ShipStranded = Sweep failed
drydock-admin-action-ClaimReleased = Retrieve failed, returned to storage
drydock-admin-action-RevisionPromoted = Revision promoted

# The Toolshed loc test requires a description key for every command, whatever the command's own
# CommandDescription attribute says, so this is the one the test reads.
command-description-drydockadmin = Opens the drydock admin panel: stored ships, berths, history, restore.
