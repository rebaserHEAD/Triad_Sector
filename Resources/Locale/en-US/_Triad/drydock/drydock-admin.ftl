# Triad: the drydock admin panel (drydockadmin). One key per control; the tooltips are the only
# tooltips in the drydock, the player tab has none.

drydock-admin-title = Drydock Administration
drydock-admin-flavor-left = SKR-OS Drydock Administration

drydock-admin-search-placeholder = Player, ship (any name it has had), ship id, or account id
drydock-admin-search-tooltip = One box. A player name, a ship name including any past name from its timeline, a ship id, or an account id.
drydock-admin-search-button = Search

drydock-admin-chip-All = All
drydock-admin-chip-Stored = Stored
drydock-admin-chip-CheckedOut = Out
drydock-admin-chip-InEscrow = Escrow
drydock-admin-chip-Sold = Sold
drydock-admin-chip-Held = Held
drydock-admin-chip-Stranded = Stranded
drydock-admin-chip-Investigating = Investigating
drydock-admin-chip-Stranded-tooltip = Checked out in a round that is over. Nothing returns these on its own; each one is a decision.

drydock-admin-count = {$total} match
drydock-admin-page = Page {$page} / {$pages}
drydock-admin-prev = Prev
drydock-admin-next = Next

drydock-admin-row-owner = owner {$owner}
drydock-admin-row-berth = #{$berth}
drydock-admin-row-live = live now
drydock-admin-row-out = out · round {$round}
drydock-admin-row-escrow = escrow · {$left}
drydock-admin-row-investigating = investigating

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
drydock-admin-hold = Hold
drydock-admin-release = Release hold
drydock-admin-hold-tooltip = Freeze the ship pending a decision. Retrieve is refused while held. Release returns it to Stored.
drydock-admin-investigate = Investigate
drydock-admin-close-investigation = Close investigation
drydock-admin-investigate-tooltip = Flag the ship. Retrieve is refused, any standing offer is withdrawn, and it refuses new offers until closed.
drydock-admin-restore-to = Restore to…
drydock-admin-restore-to-tooltip = Put a ship that is out or held back into one of the owner's berths. Refused while a live grid still carries it this round.
drydock-admin-more = ···
drydock-admin-vacate = Vacate berth
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
drydock-admin-action-Hold = Held
drydock-admin-action-Release = Hold released
drydock-admin-action-BerthPurchase = Berth purchased
drydock-admin-action-BerthSale = Berth sold
drydock-admin-action-BerthGrant = Berth granted
drydock-admin-action-BerthUpgrade = Berth upgraded
drydock-admin-action-BerthMove = Moved berth
drydock-admin-action-BerthDelete = Berth deleted
drydock-admin-action-Fallback = Fell back to an older revision
drydock-admin-action-InvestigationOpened = Investigation opened
drydock-admin-action-InvestigationClosed = Investigation closed
drydock-admin-action-AccessRefused = Access refused
drydock-admin-action-TransferOffered = Transfer offered
drydock-admin-action-TransferDeclined = Transfer declined
drydock-admin-action-TransferCancelled = Transfer withdrawn
drydock-admin-action-TransferExpired = Transfer expired
drydock-admin-action-ShipSold = Sold
drydock-admin-action-Renamed = Renamed
drydock-admin-action-SaleReversed = Sale reversed
