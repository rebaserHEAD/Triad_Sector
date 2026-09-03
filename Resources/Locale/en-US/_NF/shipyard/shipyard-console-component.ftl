## UI
shipyard-console-invalid-vessel = Cannot purchase vessel:
shipyard-console-menu-title = Shipyard Menu
shipyard-console-menu-listing-free = Free
shipyard-console-menu-listing-voucher = Voucher
shipyard-console-docking = {$owner} shuttle {$vessel} en route.
shipyard-console-leaving = {$owner} shuttle {$vessel} sold by {$player}.
shipyard-console-docking-secret = Unregistered vessel detected entering your sector.
shipyard-console-leaving-secret = Unregistered vessel detected leaving your sector.
shipyard-commands-purchase-desc = Spawns and FTL docks a specified shuttle from a grid file.
shipyard-console-no-idcard = No ID card present.
shipyard-console-already-deeded = ID card already has a Deed.
shipyard-console-invalid-station = Not a valid station.
shipyard-console-no-bank = No bank account found.
shipyard-console-no-deed = No ship deed found.
shipyard-console-sale-reqs = Ship must be docked and all crew disembarked.
shipyard-console-sale-not-docked = Ship must be docked.
shipyard-console-sale-organic-aboard = All crew must disembark. {$name} is still aboard.
# This error message is bad, but if it happens, something awful's happened.
shipyard-console-sale-invalid-ship = Ship is invalid and cannot be sold.
shipyard-console-sale-unknown-reason = Ship cannot be sold: {reason}
shipyard-console-deed-label = Registered Ship:
shipyard-console-appraisal-label = Shuttle Resale Value:{" "}
shipyard-console-no-voucher-redemptions = All voucher redemptions have been used.
shipyard-console-invalid-voucher-type = This voucher cannot be used at this console.
shipyard-console-denied = You cannot purchase this ship at this time.
shipyard-console-limited = There are too many active shuttles of this type, try again later!

shipyard-console-contraband-onboard = Smuggled contraband detected onboard.
shipyard-console-station-resources = Vital station resources detected onboard.
shipyard-console-dangerous-materials = Dangerous materials detected onboard.
shipyard-console-fallback-prevent-sale = YML-class bugs detected onboard. Please file a bug report when possible.

shipyard-console-menu-size-label = Size:{" "}
shipyard-console-menu-class-label = Class:{" "}
shipyard-console-menu-engine-label = Engine:{" "}

shipyard-console-purchase-available = Purchase
shipyard-console-guidebook = Manual
shipyard-console-unassign-deed = Unassign
shipyard-console-deed-unassigned = Deed unassigned from ID card successfully.
shipyard-console-confirm-unassign = Are you sure?
shipyard-console-unassign-cooldown = Wait {$minutes} minute(s) before unassigning another deed.

# Keep these in enum order for ease of validation.
shipyard-console-category-All = All
shipyard-console-category-Micro = Micro
shipyard-console-category-Small = Small
shipyard-console-category-Medium = Medium
shipyard-console-category-Large = Large

shipyard-console-class-All = All
shipyard-console-class-Expedition = Expedition
shipyard-console-class-Scrapyard = Scrapyard
shipyard-console-class-Salvage = Salvage
shipyard-console-class-Science = Science
shipyard-console-class-Cargo = Cargo
shipyard-console-class-Chemistry = Chemistry
shipyard-console-class-Botany = Botany
shipyard-console-class-Engineering = Engineering
shipyard-console-class-Atmospherics = Atmospherics
shipyard-console-class-Medical = Medical
shipyard-console-class-Civilian = Civilian
shipyard-console-class-Kitchen = Kitchen
# Antag
shipyard-console-class-Syndicate = Syndicate
shipyard-console-class-Pirate = PDV
# NFSD
shipyard-console-class-Capital = Capital
shipyard-console-class-Detainment = Detainment
shipyard-console-class-Detective = Detective
shipyard-console-class-Fighter = Fighter
shipyard-console-class-Patrol = Patrol
shipyard-console-class-Pursuit = Pursuit
# Mono changes start
shipyard-console-class-Corvette = Corvette
shipyard-console-class-Frigate = Frigate
shipyard-console-class-Destroyer = Destroyer
shipyard-console-class-Cruiser = Cruiser
# Mono changes end

shipyard-console-engine-All = All
shipyard-console-engine-AME = AME
shipyard-console-engine-TEG = TEG
shipyard-console-engine-Supermatter = Supermatter
shipyard-console-engine-Tesla = Tesla
shipyard-console-engine-Singularity = Singularity
shipyard-console-engine-Solar = Solar
shipyard-console-engine-RTG = RTG
shipyard-console-engine-APU = APU
shipyard-console-engine-Welding = Welding Fuel
shipyard-console-engine-Plasma = Plasma
shipyard-console-engine-Uranium = Uranium
shipyard-console-engine-Bananium = Bananium

## Error Messages
shipyard-console-load-ship-no-id = Insert an ID card to load saved ships.
shipyard-console-load-failed = Failed to load ship.
shipyard-console-insufficient-funds = Insufficient funds to load ship. Cost: {$cost} credits. Your balance: {$balance} credits.
shipyard-console-load-success-charged = Ship "{$ship}" loaded successfully. {$cost} credits charged to your account.
shipyard-console-load-success-debt = Ship "{$ship}" loaded successfully. {$cost} credits charged. WARNING: You are now {$debt} credits in debt!
# Mono start
shipyard-console-engine-NFR = NFR
# Mono end

# Triad: drydock tab
shipyard-console-tab-purchase = Purchase
shipyard-console-tab-drydock = Drydock
shipyard-console-retrieve-button = Retrieve
shipyard-console-store-in-button = Store in #{$berth}
shipyard-console-store-no-fit-button = No berth fits
shipyard-console-store-success = Ship stored. Retrieve it from any shipyard console.
shipyard-console-store-not-owner = That ship is registered to another account.
shipyard-console-not-owner = That is registered to another account.
shipyard-console-lockout-title = ACCESS DENIED
shipyard-console-lockout-subtitle = BIOMETRIC MISMATCH
shipyard-console-lockout-body = This card is registered to another operator.
shipyard-console-store-organics = Someone is still aboard. Everyone has to be off the ship.
shipyard-console-store-hazard = Something aboard is armed or unstable. Make it safe first.
shipyard-console-store-disabled = The drydock is not accepting ships right now.
shipyard-console-store-failed = The drydock could not store that ship. Try again shortly.
shipyard-console-store-no-berth = Your drydock has no free berth. Buy one, or retrieve and sell a ship.
shipyard-console-store-berth-too-small = None of your free berths can take a hull this size. Upgrade a berth or buy a larger one.
shipyard-console-store-in-progress = That ship is already being stored.
shipyard-console-deed-ship-out = {$ship} · {$class} · out {$minutes} m
shipyard-console-deed-ship-new = {$ship} · {$class} · never stored
shipyard-console-berths-free = {$free} of {$total} berths free
shipyard-console-berth-row = #{$berth}  {$class}
shipyard-console-berth-occupant = {$ship} · {$class}
shipyard-console-berth-empty = empty
shipyard-console-berth-buy-button = Buy berth
shipyard-console-menu-transfer = Transfer…
shipyard-console-menu-upgrade-berth = Upgrade berth
shipyard-console-menu-sell-berth = Sell berth
shipyard-console-menu-occupied = occupied
shipyard-console-berth-bought = {$class} berth added to your drydock.
shipyard-console-berth-sold = Berth sold for {$refund}.
shipyard-console-berth-upgraded = Berth upgraded to {$class}.
shipyard-console-berth-unaffordable = Not enough funds: that costs {$cost}.
shipyard-console-berth-occupied = That berth has a ship in it.
shipyard-console-berth-failed = The drydock could not change that berth. Try again shortly.
shipyard-console-transfer-accept-button = Accept
shipyard-console-transfer-confirm-button = Confirm: this takes your last free berth
shipyard-console-transfer-cancel-button = Cancel offer
shipyard-console-transfer-offer = {$owner} offers {$ship} · {$class} · {$seconds} s left
shipyard-console-transfer-warning = Last free berth while you have a ship out. It would have nowhere to dock.
shipyard-console-transfer-offered = Offer made. The recipient has {$seconds} seconds to insert their card and accept.
shipyard-console-transfer-busy = Another offer is waiting at this console.
shipyard-console-transfer-not-verified = The console could not verify you. Only the character at the console may transfer or accept.
shipyard-console-transfer-not-yours = That ship is not yours to give, or it is not in the drydock.
shipyard-console-transfer-none = There is no offer waiting.
shipyard-console-transfer-own = You cannot accept your own offer.
shipyard-console-transfer-gone = That ship is no longer available.
shipyard-console-transfer-failed = The transfer could not be completed. Try again shortly.
shipyard-console-transfer-complete = {$ship} is now in your drydock.
shipyard-console-retrieve-success = Ship retrieved and docked.
shipyard-console-retrieve-failed = That ship could not be retrieved. It may already be out.
