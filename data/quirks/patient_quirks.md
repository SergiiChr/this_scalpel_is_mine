# Patient quirks

Source of truth for patient quirks. The game reads this file directly at startup.

Format rules:
- `## id` starts a quirk. The id is used in code and save files, so keep it stable.
- `- key: value` lines fill it in. One line per key.
- Quirks with `variants` (like weak/strong) can override any key per variant: `- cons.weak: ...`.
- `icon` is a relative markdown link, ctrl+click it in most editors to open the image.
- `effects` is what the game actually reads: `key=value` pairs separated by commas.
  Keys ending in `_mult` multiply, other numbers add up, text values are names (use `|` for lists).
  See `docs/DESIGN.md` for every effect key the code understands.
- `card` is what shows up on the patient card. `red_herring` quirks show a card line with no gameplay effect.
- `sites` (optional) limits the quirk to scenarios on those surgical sites.

## diabetes
- name: Diabetes
- polarity: negative
- icon: [diabetes.svg](../../assets/icons/quirks/patient/diabetes.svg)
- lore: Carries a glucose meter with a cracked screen and a pocket full of candy wrappers.
- pros: None.
- cons: Blood glucose creeps up during surgery. Too high and they fade out, too low and they seize.
- specifics: Insulin lowers glucose, glucose (D50) raises it. Stress makes it climb faster. Blood panel shows the exact value.
- card: Type 1 diabetic, insulin dependent.
- effects: glucose_drift=0.006

## epilepsy
- name: Epilepsy
- polarity: negative
- icon: [epilepsy.svg](../../assets/icons/quirks/patient/epilepsy.svg)
- lore: Stopped taking the meds three weeks ago. Said they made everything taste like pennies.
- pros: None.
- cons: Random seizures jolt the table, shake every hand in contact and can knock tools loose.
- specifics: Diazepam stops an active seizure and suppresses new ones while it lasts. Low glucose also triggers seizures.
- card: History of epileptic seizures.
- effects: seizure_chance=0.004

## regeneration
- name: Regeneration
- polarity: mixed
- icon: [regeneration.svg](../../assets/icons/quirks/patient/regeneration.svg)
- lore: The lab that made them burned down. Their scars didn't stay long enough to be photographed.
- pros: Every wound slowly closes on its own, including the ones you made by mistake.
- cons: Your incisions close too. Take too long inside and you have to cut your way back in.
- specifics: Closure speed is slow on purpose. Retracted wounds close slower while held open.
- card: Unusually fast wound healing noted on a previous admission. Declined further tests.
- effects: heal_rate=0.004

## coagulation
- name: Coagulation
- variants: weak, strong
- polarity.weak: negative
- polarity.strong: mixed
- icon: [coagulation.svg](../../assets/icons/quirks/patient/coagulation.svg)
- lore.weak: Bruises from a handshake. Nosebleeds from bad news.
- lore.strong: Blood like cold syrup. Their last doctor called it "impressive" and then stopped calling.
- pros.weak: Low risk of clots, blood thinners are rarely needed.
- pros.strong: Bleeds a lot less.
- cons.weak: Bleeds 80% faster. Every nick matters.
- cons.strong: Clots can form and cause a stroke or heart attack. Heparin prevents it.
- specifics: Tranexamic acid (TXA) slows bleeding, heparin thins blood. Using the wrong one makes things worse.
- card.weak: Mild hemophilia.
- card.strong: Thrombophilia, prior DVT.
- effects.weak: bleed_mult=1.8
- effects.strong: bleed_mult=0.55, clot_risk=0.003

## bones
- name: Bone density
- variants: strong, fragile
- polarity.strong: mixed
- polarity.fragile: negative
- icon: [bones.svg](../../assets/icons/quirks/patient/bones.svg)
- lore.strong: Fell off a four storey building. Walked to the hospital. Complained about the wait.
- lore.fragile: Sneezed and cracked a rib. Twice.
- pros.strong: Bones never break by accident.
- pros.fragile: Bone saws go through twice as fast.
- cons.strong: A regular bone saw can't cut them. Request the heavy saw or use a mallet.
- cons.fragile: Dropping anything heavy on them breaks a bone.
- specifics: Heavy saw is orderable from the nurse. Mallet works on strong bones but bruises everything around.
- card.strong: Unusually high bone density on previous X-ray.
- card.fragile: Osteoporosis.
- effects.strong: bone_hardness=2
- effects.fragile: bone_fragile=1, saw_speed_mult=2

## allergy
- name: Drug allergy
- variants: cefazolin, morphine, lidocaine, propofol
- polarity: negative
- icon: [allergy.svg](../../assets/icons/quirks/patient/allergy.svg)
- lore: Their allergy wristband fell off in the ambulance. Probably.
- pros: None.
- cons: Giving the allergen causes swelling, a blood pressure crash and possibly anaphylaxis.
- specifics: Antihistamine handles mild reactions. Anaphylaxis needs adrenaline. The manual lists safe substitutes.
- card: Drug allergy reported, agent not documented.
- effects.cefazolin: allergen=cefazolin
- effects.morphine: allergen=morphine
- effects.lidocaine: allergen=lidocaine
- effects.propofol: allergen=propofol

## aneurysm
- name: Aneurysm
- polarity: negative
- icon: [aneurysm.svg](../../assets/icons/quirks/patient/aneurysm.svg)
- lore: Their last scan came with a phone call and the words "don't lift anything heavy".
- pros: None.
- cons: A weak, ballooned artery runs under the site. High blood pressure can burst it and start a deep internal bleed.
- specifics: Pressure rises with panic and stimulants (adrenaline, cocaine, ketamine). Keep it below 140 mmHg.
- card: Known history of aneurysm.
- sites: chest, abdomen, thigh, neck
- effects: fragile_vessels=1

## heart
- name: Heart
- variants: weak, strong
- polarity.weak: negative
- polarity.strong: positive
- icon: [heart.svg](../../assets/icons/quirks/patient/heart.svg)
- lore.weak: Two stents and a vape.
- lore.strong: Resting heart rate of a sleeping bear.
- pros.strong: Much less likely to go into cardiac arrest.
- cons.weak: Blood loss, pain and bad drug combinations push them into arrest far easier.
- specifics: Keep the defibrillator close. Amiodarone improves shock success.
- card.weak: Coronary artery disease.
- card.strong: Marathon runner.
- effects.weak: arrest_mult=2.5
- effects.strong: arrest_mult=0.4

## blood_count
- name: Blood count
- variants: high, low
- polarity.high: mixed
- polarity.low: negative
- icon: [blood_count.svg](../../assets/icons/quirks/patient/blood_count.svg)
- lore.high: Lives up a mountain. Refuses to explain which one.
- lore.low: Donates blood for cash. Every week.
- pros.high: Starts with more blood, can lose more before crashing.
- cons.high: Thicker blood, small clot risk.
- cons.low: Starts with 20% less blood. Have blood packs ready.
- specifics: Blood panel "counts" reveals this.
- card.low: Anemic.
- effects.high: blood_ml_mult=1.2, clot_risk=0.002
- effects.low: blood_ml_mult=0.8

## rare_blood
- name: Rare blood
- polarity: negative
- icon: [rare_blood.svg](../../assets/icons/quirks/patient/rare_blood.svg)
- lore: One in four million. The blood bank has two bags and a waiting list.
- pros: None.
- cons: Only the rare blood pack is compatible, O-negative included. It takes the nurse extra long to fetch.
- specifics: Order it early.
- card: Bombay phenotype.
- effects: rare_blood=1

## anesthesia_resistant
- name: Anesthesia resistant
- polarity: negative
- icon: [anesthesia_resistant.svg](../../assets/icons/quirks/patient/anesthesia_resistant.svg)
- lore: Woke up during their last three surgeries. Remembers the jokes.
- pros: None.
- cons: Anesthesia wears off twice as fast. They wake up talking, then screaming.
- specifics: Watch consciousness on the monitor and top up in time.
- card: Previous intraoperative awareness.
- effects: anesthesia_decay_mult=2.2

## chatterbox
- name: Chatterbox
- polarity: mixed
- icon: [chatterbox.svg](../../assets/icons/quirks/patient/chatterbox.svg)
- lore: Has a podcast. Nobody listens. You will.
- pros: Awake, they describe what they feel. Some of it points at hidden conditions.
- cons: Constant chatter fills the subtitles. Some of it is lies.
- specifics: Only matters while the patient is conscious.
- effects: talk_rate=3

## panicker
- name: Panicker
- polarity: negative
- icon: [panicker.svg](../../assets/icons/quirks/patient/panicker.svg)
- lore: Fainted at their own blood test.
- pros: None.
- cons: Panic builds twice as fast. A panicking patient flails and bumps your hands.
- specifics: Talk to them at the head of the bed (interact) or sedate them.
- card: Anxiety disorder.
- effects: panic_mult=2

## obese
- name: Obese
- polarity: negative
- icon: [obese.svg](../../assets/icons/quirks/patient/obese.svg)
- lore: Food critic. Worked hard for this.
- pros: None.
- cons: Thick fat layer. You need deeper cuts to open and stronger retraction to keep them open.
- specifics: Use deep pressure when cutting. Retractors work better than forceps here.
- card: BMI 41.
- effects: fat_depth=1

## mirrored
- name: Mirrored organs
- polarity: neutral
- icon: [mirrored.svg](../../assets/icons/quirks/patient/mirrored.svg)
- lore: Everything's on the wrong side. Including their opinions.
- pros: None.
- cons: Organs and targets are mirrored left to right. Incisions planned by the book land on the wrong side.
- specifics: Patient card mentions "dextrocardia" if you read it. Mark after you confirm.
- card: Dextrocardia (situs inversus).
- effects: mirrored=1

## smoker
- name: Heavy smoker
- polarity: negative
- icon: [smoker.svg](../../assets/icons/quirks/patient/smoker.svg)
- lore: Two packs a day since the eighth grade.
- pros: None.
- cons: Lower oxygen saturation. Coughing fits jolt the table.
- specifics: Anesthesia stops the coughing.
- card: Smoker, 40 pack-years.
- effects: spo2_offset=-4, cough_chance=0.006

## alcoholic
- name: Alcoholic
- polarity: mixed
- icon: [alcoholic.svg](../../assets/icons/quirks/patient/alcoholic.svg)
- lore: Knows every bartender in the district by first name and blood type.
- pros: Whiskey calms them down without the usual side effects.
- cons: Sedatives and anesthesia are 40% weaker. Bleeds a little more.
- specifics: Double check anesthesia depth.
- card: Alcohol use disorder.
- effects: sedation_mult=0.6, bleed_mult=1.2, whiskey_friendly=1

## pacemaker
- name: Pacemaker
- polarity: mixed
- icon: [pacemaker.svg](../../assets/icons/quirks/patient/pacemaker.svg)
- lore: Model discontinued in 2009. Serial number scratched off.
- pros: Half as likely to go into cardiac arrest.
- cons: Cautery or defibrillation near the chest makes the pacemaker misfire.
- specifics: Use staples or sutures instead of cautery on the chest.
- card: Implanted pacemaker.
- effects: pacemaker=1, arrest_mult=0.5

## malignant_hyperthermia
- name: Malignant hyperthermia
- polarity: negative
- icon: [malignant_hyperthermia.svg](../../assets/icons/quirks/patient/malignant_hyperthermia.svg)
- lore: Their uncle died on an operating table. Nobody told them why.
- pros: None.
- cons: Anesthetic gas makes body temperature spike fast. Untreated, it kills.
- specifics: Use propofol instead of gas. Dantrolene stops a crisis.
- card: Family history of anesthesia complications.
- effects: mh_trigger=1

## thin_skin
- name: Thin skin
- polarity: negative
- icon: [thin_skin.svg](../../assets/icons/quirks/patient/thin_skin.svg)
- lore: Ninety years of sun. Skin like wet paper.
- pros: None.
- cons: Tears at half the usual pull and bruises twice as easily.
- specifics: Use retractors gently. Tape closures hold poorly.
- card: Fragile skin, tears easily.
- effects: tear_threshold_mult=0.5, bruise_mult=2

## ticklish
- name: Ticklish
- polarity: mixed
- icon: [ticklish.svg](../../assets/icons/quirks/patient/ticklish.svg)
- lore: Laughed through their own appendectomy. The surgeon didn't.
- pros: Laughter makes pain 30% easier to bear.
- cons: Awake patient flinches when tools touch outside the surgical site.
- specifics: Only matters while the patient is conscious.
- effects: ticklish=1, pain_mult=0.7

## tattooed
- name: Prison tattoos
- polarity: neutral
- icon: [tattooed.svg](../../assets/icons/quirks/patient/tattooed.svg)
- lore: Every tattoo is a story. Some are also a map.
- pros: None.
- cons: None.
- specifics: Red herring. Looks important, does nothing.
- card: Extensive tattoos.
- red_herring: true
- effects: none=0
