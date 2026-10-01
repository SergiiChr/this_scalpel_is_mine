class_name ToolDef
extends RefCounted
## One entry of data/tools.cfg. Field meaning is documented at the top of that file.

var id: String
var name: String
var action: String
var description: String
var sharpness: float
var quality: float
var radius: float
var power: float
var sterile: bool
var size: String
var improvised: bool
var orderable: bool
var delay: float
var category: String
var charges: int
var drug: String
var volume: float
var concentration: float
var fragile: bool
var drinkable: bool
var iv_only: bool
var tall: bool
var self_retaining: bool
## Stays where it is: no hand picks it up (the IV drip on its stand).
var fixed: bool
var model: String
var length: float
var width: float
var color: Color
var grip: String
## Group it's laid out with on the instrument tray at the start (Room.TRAY_ZONES), "" for the space left.
var tray: String


static func from_config(cfg: ConfigFile, section: String) -> ToolDef:
	var def := ToolDef.new()
	def.id = section
	def.name = cfg.get_value(section, "name", section.capitalize())
	def.action = cfg.get_value(section, "action", "none")
	def.description = cfg.get_value(section, "description", "")
	def.sharpness = cfg.get_value(section, "sharpness", 1.0)
	def.quality = cfg.get_value(section, "quality", 1.0)
	def.radius = cfg.get_value(section, "radius", 0.01)
	def.power = cfg.get_value(section, "power", 1.0)
	def.sterile = cfg.get_value(section, "sterile", false)
	def.size = cfg.get_value(section, "size", "normal")
	def.improvised = cfg.get_value(section, "improvised", false)
	def.orderable = cfg.get_value(section, "orderable", false)
	def.delay = cfg.get_value(section, "delay", 20.0)
	def.category = cfg.get_value(section, "category", "Supplies")
	def.charges = cfg.get_value(section, "charges", -1)
	def.drug = cfg.get_value(section, "drug", "")
	def.volume = cfg.get_value(section, "volume", 0.0)
	def.concentration = cfg.get_value(section, "concentration", 0.0)
	def.fragile = cfg.get_value(section, "fragile", false)
	def.drinkable = cfg.get_value(section, "drinkable", false)
	def.iv_only = cfg.get_value(section, "iv_only", false)
	def.tall = cfg.get_value(section, "tall", false)
	def.self_retaining = cfg.get_value(section, "self_retaining", false)
	def.fixed = cfg.get_value(section, "fixed", false)
	def.model = cfg.get_value(section, "model", "")
	def.length = cfg.get_value(section, "length", 0.12)
	def.width = cfg.get_value(section, "width", 0.012)
	def.color = cfg.get_value(section, "color", Color.GRAY)
	def.grip = cfg.get_value(section, "grip", "pencil")
	var tray_by_action: String = {"syringe": "syringes", "vial": "bottles", "pour": "bottles"}.get(def.action, "")
	def.tray = cfg.get_value(section, "tray", tray_by_action)
	return def
