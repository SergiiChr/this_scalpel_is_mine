# Surgeon quirks

Source of truth for surgeon quirks. The game reads this file directly at startup.
Same format as `patient_quirks.md`.

Rolling rules (code in `src/data/quirk_roller.gd`):
- Every surgeon gets 1 to 3 quirks.
- With 3 quirks at least one is positive and one is negative. `mixed` counts as both.
- `exclusive: true` quirks are always rolled alone.
- `items` are personal tools that spawn on your belt. Ids come from `data/tools.cfg`.

## shaky_hands
- name: Shaky hands
- polarity: negative
- icon: [shaky_hands.svg](../../assets/icons/quirks/surgeon/shaky_hands.svg)
- lore: Twelve years of night shifts. The coffee doesn't help anymore.
- pros: None.
- cons: Your hands tremble all the time: stress never drains below 65%.
- specifics: Hold breath to steady them. The breath meter drains while held and refills slowly. A small dose of diazepam in your other hand stops the shaking for a while.
- effects: stress_floor=0.65

## steady_hands
- name: Steady hands
- polarity: positive
- icon: [steady_hands.svg](../../assets/icons/quirks/surgeon/steady_hands.svg)
- lore: Defused things in another life. Threads needles on a moving train.
- pros: Your hands never shake: not from stress, cold, coffee or any other quirk.
- cons: None.
- specifics: Stress still builds and still makes you pass out when full.
- effects: tremor_mult=0

## divine_knowledge
- name: Divine knowledge
- polarity: positive
- icon: [divine_knowledge.svg](../../assets/icons/quirks/surgeon/divine_knowledge.svg)
- lore: You dreamt of this patient last night. You don't talk about the dream.
- pros: Manual pages relevant to this patient glow.
- cons: None.
- specifics: Highlights are based on hidden patient conditions, not only the ones on the card.
- effects: manual_highlight=1

## bump_resistant
- name: Iron grip
- polarity: positive
- icon: [bump_resistant.svg](../../assets/icons/quirks/surgeon/bump_resistant.svg)
- lore: Former bouncer. Nothing leaves your hands unless you say so.
- pros: 80% less likely to drop tools when bumped, jolted or startled.
- cons: None.
- specifics: Does not help against passing out or grip loss from sweat.
- effects: bump_resist=0.8

## hand_size
- name: Hand size
- variants: big, small
- polarity: mixed
- icon: [hand_size.svg](../../assets/icons/quirks/surgeon/hand_size.svg)
- lore.big: Gloves size XXL, special order, always late.
- lore.small: The nurses call you "the tweezers".
- pros.big: Steadier under bumps and pulls harder with clamps and retractors.
- pros.small: Much more precise, tremor reduced.
- cons.big: Can't use fine tools (suture needle, marker, syringes).
- cons.small: Can't use heavy tools (heavy saw, mallet, defibrillator paddles).
- specifics: Fine and heavy tools are tagged in the tool list and in the manual. Big weighs 100 kg, small 60 kg: drug doses for you scale with it.
- effects.big: fine_tools_blocked=1, bump_resist=0.3, grip_strength_mult=1.5, weight_kg=20
- effects.small: heavy_tools_blocked=1, tremor_mult=0.4, weight_kg=-20

## normal_dude
- name: Normal dude
- polarity: neutral
- exclusive: true
- icon: [normal_dude.svg](../../assets/icons/quirks/surgeon/normal_dude.svg)
- lore: Went to med school. Graduated. Pays taxes. Unsettling.
- pros: No flaws.
- cons: No perks.
- specifics: Always rolled alone.
- effects: none=0

## alcoholic
- name: Alcoholic
- polarity: mixed
- icon: [alcoholic.svg](../../assets/icons/quirks/surgeon/alcoholic.svg)
- lore: The flask is "for disinfecting". Mostly the inside.
- pros: Starts with a whiskey flask. A sip calms nerves and stops the tremor for a while.
- cons: Slight tremor between sips: stress never drains below 35%. Bad breath makes your partner sick if you stand too close for too long.
- specifics: Whiskey also sanitizes tools and wounds, badly.
- effects: items=whiskey_flask, stress_floor=0.35, bad_breath=0.03, drink_steady=1

## smoker
- name: Smoker
- polarity: mixed
- icon: [smoker.svg](../../assets/icons/quirks/surgeon/smoker.svg)
- lore: Quit six times. Today is not a quitting day.
- pros: Starts with a lighter (sloppy cauterizing) and a pack of cigarettes. A smoke stops all stress for 3 minutes and makes you 20% faster.
- cons: Stress builds 40% faster. Bad breath. Occasional coughing fits jerk your active hand.
- specifics: Smoke only at the smoking spot in the corner, pack in hand. Lighter burns are wide and hurt more.
- effects: items=lighter|cig_pack, bad_breath=0.02, cough_chance=0.003, stress_mult=1.4

## sweaty
- name: Sweaty
- polarity: negative
- icon: [sweaty.svg](../../assets/icons/quirks/surgeon/sweaty.svg)
- lore: Sweats through scrubs in a freezer. Has been tested. Nothing.
- pros: None.
- cons: Gloves get slippery over time and tools start slipping out. Sweat drips into open wounds.
- specifics: Change gloves at the glove box. Request a surgical cap from the nurse to stop the drip.
- effects: sweat_rate=0.004

## hemophobia
- name: Hemophobia
- polarity: negative
- icon: [hemophobia.svg](../../assets/icons/quirks/surgeon/hemophobia.svg)
- lore: Chose surgery to face the fear. The fear is winning.
- pros: None.
- cons: Active bleeding fills your sickness gauge. Full gauge means vomiting.
- specifics: Stop bleeding fast. Looking away helps a little.
- effects: blood_sickness_rate=0.02

## nerves
- name: Nerves
- variants: weak, steel
- polarity.weak: negative
- polarity.steel: positive
- icon: [nerves.svg](../../assets/icons/quirks/surgeon/nerves.svg)
- lore.weak: Lightheaded. Passed out at your own graduation.
- lore.steel: Did a tracheotomy on a bus with a biro. Still has the biro.
- pros.steel: Stress builds 60% slower.
- cons.weak: Stress builds 80% faster. Full stress means passing out.
- specifics: Every mistake, alarm and scream adds stress. Calm periods let it drain.
- effects.weak: stress_mult=1.8
- effects.steel: stress_mult=0.4

## handyman
- name: Handyman
- polarity: positive
- icon: [handyman.svg](../../assets/icons/quirks/surgeon/handyman.svg)
- lore: Weekend carpenter. Sees a femur, thinks "load bearing".
- pros: Starts with a screwdriver and duct tape.
- cons: None.
- specifics: Screwdriver works as a chisel on bone. Duct tape closes anything, badly.
- effects: items=screwdriver|duct_tape

## office_drone
- name: Office drone
- polarity: mixed
- icon: [office_drone.svg](../../assets/icons/quirks/surgeon/office_drone.svg)
- lore: Came from middle management. Kept the stapler.
- pros: Starts with an office stapler and paper clips.
- cons: One fewer belt slot, the pockets are full of pens.
- specifics: Office staples can tear under tension and cause internal bleeding later.
- effects: items=office_stapler|paper_clips, belt_slots=-1

## back_alley
- name: Back alley surgeon
- polarity: mixed
- icon: [back_alley.svg](../../assets/icons/quirks/surgeon/back_alley.svg)
- lore: Lost the license. Kept the patients.
- pros: Starts with a switchblade and a metal straw. Improvised tools work 50% better for you.
- cons: The nurse doesn't trust you. Your requests take 50% longer.
- specifics: Sanitize the switchblade before use.
- effects: items=switchblade|metal_straw, improvised_mult=0.5, nurse_delay_mult=1.5

## no_pants
- name: No pants
- polarity: negative
- icon: [no_pants.svg](../../assets/icons/quirks/surgeon/no_pants.svg)
- lore: Long story. Nobody asks twice.
- pros: Moves 15% faster.
- cons: No belt at all. Everything goes on the tray or stays in your hands.
- specifics: Personal items spawn on the tray instead.
- effects: belt_slots=-4, move_speed_mult=1.15

## ambidextrous
- name: Ambidextrous
- polarity: positive
- icon: [ambidextrous.svg](../../assets/icons/quirks/surgeon/ambidextrous.svg)
- lore: Writes with both hands. Signs checks with both hands. Different names.
- pros: Switching hands is instant and the idle hand never drifts.
- cons: None.
- specifics: Everyone else has a short switch delay.
- effects: switch_delay_mult=0

## iron_stomach
- name: Iron stomach
- polarity: positive
- icon: [iron_stomach.svg](../../assets/icons/quirks/surgeon/iron_stomach.svg)
- lore: Worked a summer at a meat plant. Ate lunch there too.
- pros: Immune to sickness from blood, bad breath and vomit.
- cons: None.
- specifics: Still vomits if poisoned. Nothing in the game poisons you. Yet.
- effects: sickness_immune=1

## germaphobe
- name: Germaphobe
- polarity: mixed
- icon: [germaphobe.svg](../../assets/icons/quirks/surgeon/germaphobe.svg)
- lore: Sees bacteria when closing their eyes.
- pros: Unsterile tools glow a sickly green for you.
- cons: Touching an unsterile tool spikes your stress.
- specifics: Sanitize at the alcohol bath.
- effects: contamination_vision=1, dirty_stress=0.12

## caffeine
- name: Caffeine addict
- polarity: mixed
- icon: [caffeine.svg](../../assets/icons/quirks/surgeon/caffeine.svg)
- lore: Blood type: espresso.
- pros: Starts with a coffee thermos. A sip makes your hands 30% faster for a minute.
- cons: Without coffee your hands slowly get sluggish.
- specifics: Coffee also keeps stress 15% higher while it works: a tiny tremor. Worth it.
- effects: items=coffee_thermos, caffeine=1

## former_medic
- name: Former combat medic
- polarity: positive
- icon: [former_medic.svg](../../assets/icons/quirks/surgeon/former_medic.svg)
- lore: Did this in worse places. Doesn't talk about which.
- pros: Wider timing windows when turning the patient. Defibrillator charges twice as fast.
- cons: None.
- specifics: Stacks with a partner's timing.
- effects: qte_window_mult=1.5, defib_charge_mult=0.5

## hard_of_hearing
- name: Hard of hearing
- polarity: negative
- icon: [hard_of_hearing.svg](../../assets/icons/quirks/surgeon/hard_of_hearing.svg)
- lore: Front row at too many concerts. Worth it.
- pros: None.
- cons: You can't hear the monitor or alarms. Patient speech shows as subtitles only.
- specifics: Watch the screen. Your partner has to shout (they don't know that).
- effects: deaf=1
