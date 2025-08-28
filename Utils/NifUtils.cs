using NiflySharp;
using NiflySharp.Enums;
using NiflySharp.Structs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using static NiflySharp.Enums.SkyrimHavokMaterial;

namespace SECmd.Utils
{
	 static internal class NifUtils
	 {
		  internal static Color3 GetMaterialColor(SkyrimHavokMaterial material)
		  {
				switch (material)
				{
					 //#declare Aquamarine = color red 0.439216 green 0.858824 blue 0.576471
					 case SKY_HAV_MAT_BROKEN_STONE: return new(0.439216f, 0.858824f, 0.576471f);
					 //#declare CoolCopper = color red 0.85 green 0.53 blue 0.10
					 case SKY_HAV_MAT_LIGHT_WOOD: return new(0.85f, 0.53f, 0.10f);
					 //#declare SummerSky = color red 0.22 green 0.69 blue 0.87
					 case SKY_HAV_MAT_SNOW: return new(0.22f, 0.69f, 0.87f);
					 //#declare DarkSlateGray = color red 0.184314 green 0.309804 blue 0.309804
					 case SKY_HAV_MAT_GRAVEL: return new(0.184314f, 0.309804f, 0.309804f);
					 //#declare Brass = color red 0.71 green 0.65 blue 0.26
					 case SKY_HAV_MAT_MATERIAL_CHAIN_METAL: return new(0.71f, 0.65f, 0.26f);
					 //#declare DarkOliveGreen = color red 0.309804 green 0.309804 blue 0.184314
					 case SKY_HAV_MAT_BOTTLE: return new(0.309804f, 0.309804f, 0.184314f);
					 //#declare MediumWood = color red 0.65 green 0.50 blue 0.39
					 case SKY_HAV_MAT_WOOD: return new(0.65f, 0.50f, 0.39f);
					 //#declare Flesh = color red 0.96 green 0.80 blue 0.69
					 case SKY_HAV_MAT_SKIN: return new(0.96f, 0.80f, 0.69f);
					 //#declare Scarlet = color red 0.55 green 0.09 blue 0.09
					 case SKY_HAV_MAT_UNKNOWN_617099282: return new(0.55f, 0.09f, 0.09f);
					 //#declare Maroon = color red 0.556863 green 0.137255 blue 0.419608
					 case SKY_HAV_MAT_BARREL: return new(0.556863f, 0.137255f, 0.419608f);
					 //#declare Quartz = color red 0.85 green 0.85 blue 0.95
					 case SKY_HAV_MAT_MATERIAL_CERAMIC_MEDIUM: return new(0.85f, 0.85f, 0.95f);
					 //#declare Khaki = color red 0.623529 green 0.623529 blue 0.372549
					 case SKY_HAV_MAT_MATERIAL_BASKET: return new(0.623529f, 0.623529f, 0.372549f);
					 //#declare LightBlue = color red 0.74902 green 0.847059 blue 0.847059
					 case SKY_HAV_MAT_ICE: return new(0.74902f, 0.847059f, 0.847059f);
					 //#declare Silver = color red 0.90 green 0.91 blue 0.98
					 case SKY_HAV_MAT_STAIRS_STONE: return new(0.90f, 0.91f, 0.98f);
					 //#declare MediumAquamarine = color red 0.196078 green 0.8 blue 0.6
					 case SKY_HAV_MAT_WATER: return new(0.196078f, 0.8f, 0.6f);
					 //#declare NewTan = color red 0.92 green 0.78 blue 0.62
					 case SKY_HAV_MAT_UNKNOWN_1028101969: return new(0.92f, 0.78f, 0.62f);
					 //#declare NavyBlue = color red 0.137255 green 0.137255 blue 0.556863
					 case SKY_HAV_MAT_MATERIAL_BLADE_1HAND: return new(0.137255f, 0.137255f, 0.556863f);
					 //#declare BakersChoc = color red 0.36 green 0.20 blue 0.09
					 case SKY_HAV_MAT_MATERIAL_BOOK: return new(0.36f, 0.20f, 0.09f);
					 //#declare Sienna = color red 0.556863 green 0.419608 blue 0.137255
					 case SKY_HAV_MAT_MATERIAL_CARPET: return new(0.556863f, 0.419608f, 0.137255f);
					 //#declare Feldspar = color red 0.82 green 0.57 blue 0.46
					 case SKY_HAV_MAT_SOLID_METAL: return new(0.82f, 0.57f, 0.46f);
					 //#declare DkGreenCopper = color red 0.29 green 0.46 blue 0.43
					 case SKY_HAV_MAT_MATERIAL_AXE_1HAND: return new(0.29f, 0.46f, 0.43f);
					 //#declare Very_Light_Purple = colour red 0.94 green 0.81 blue 0.99
					 case SKY_HAV_MAT_UNKNOWN_1440721808: return new(0.94f, 0.81f, 0.99f);
					 //#declare YellowGreen = color red 0.6 green 0.8 blue 0.196078
					 case SKY_HAV_MAT_STAIRS_WOOD: return new(0.6f, 0.8f, 0.196078f);
					 //#declare DarkWood = color red 0.52 green 0.37 blue 0.26
					 case SKY_HAV_MAT_MUD: return new(0.52f, 0.37f, 0.26f);
					 //#declare Plum = color red 0.917647 green 0.678431 blue 0.917647
					 case SKY_HAV_MAT_MATERIAL_BOULDER_SMALL: return new(0.917647f, 0.678431f, 0.917647f);
					 //#declare SkyBlue = color red 0.196078 green 0.6 blue 0.8
					 case SKY_HAV_MAT_STAIRS_SNOW: return new(0.196078f, 0.6f, 0.8f);
					 //#declare DarkTan = color red 0.59 green 0.41 blue 0.31
					 case SKY_HAV_MAT_HEAVY_STONE: return new(0.59f, 0.41f, 0.31f);
					 //#declare Med_Purple = colour red 0.73 green 0.16 blue 0.96
					 case SKY_HAV_MAT_UNKNOWN_1574477864: return new(0.73f, 0.16f, 0.96f);
					 //#declare Light_Purple = colour red 0.87 green 0.58 blue 0.98
					 case SKY_HAV_MAT_UNKNOWN_1591009235: return new(0.87f, 0.58f, 0.98f);
					 //#declare DarkBrown = color red 0.36 green 0.25 blue 0.20
					 case SKY_HAV_MAT_MATERIAL_BOWS_STAVES: return new(0.36f, 0.25f, 0.20f);
					 //#declare Bronze2 = color red 0.65 green 0.49 blue 0.24
					 case SKY_HAV_MAT_MATERIAL_WOOD_AS_STAIRS: return new(0.65f, 0.49f, 0.24f);
					 //#declare SpringGreen = color green 1.0 blue 0.498039
					 case SKY_HAV_MAT_GRASS: return new(0.0f, 1.0f, 0.498039f);
					 //#declare OrangeRed = color red 1.0 green 0.25
					 case SKY_HAV_MAT_MATERIAL_BOULDER_LARGE: return new(1.0f, 0.25f, 0.0f);
					 //#declare DustyRose = color red 0.52 green 0.39 blue 0.39
					 case SKY_HAV_MAT_MATERIAL_STONE_AS_STAIRS: return new(0.52f, 0.39f, 0.39f);
					 //#declare NewMidnightBlue = color red 0.00 green 0.00 blue 0.61
					 case SKY_HAV_MAT_MATERIAL_BLADE_2HAND: return new(0.0f, 0.0f, 0.61f);
					 //#declare SeaGreen = color red 0.137255 green 0.556863 blue 0.419608
					 case SKY_HAV_MAT_MATERIAL_BOTTLE_SMALL: return new(0.137255f, 0.556863f, 0.419608f);
					 //#declare Wheat = color red 0.847059 green 0.847059 blue 0.74902
					 case SKY_HAV_MAT_SAND: return new(0.847059f, 0.847059f, 0.74902f);
					 //#declare SteelBlue = color red 0.137255 green 0.419608 blue 0.556863
					 case SKY_HAV_MAT_HEAVY_METAL: return new(0.137255f, 0.419608f, 0.556863f);
					 //#declare SpicyPink = color red 1.00 green 0.11 blue 0.68
					 case SKY_HAV_MAT_UNKNOWN_2290050264: return new(1.0f, 0.11f, 0.68f);
					 //#declare BrightGold = color red 0.85 green 0.85 blue 0.10
					 case SKY_HAV_MAT_DRAGON: return new(0.85f, 0.85f, 0.10f);
					 //#declare GreenCopper = color red 0.32 green 0.49 blue 0.46
					 case SKY_HAV_MAT_MATERIAL_BLADE_1HAND_SMALL: return new(0.32f, 0.49f, 0.46f);
					 //#declare NeonPink = color red 1.00 green 0.43 blue 0.78
					 case SKY_HAV_MAT_MATERIAL_SKIN_SMALL: return new(1.0f, 0.43f, 0.78f);
					 //#declare SemiSweetChoc = color red 0.42 green 0.26 blue 0.15
					 case SKY_HAV_MAT_STAIRS_BROKEN_STONE: return new(0.42f, 0.26f, 0.15f);
					 //#declare MandarinOrange = color red 0.89 green 0.47 blue 0.20
					 case SKY_HAV_MAT_MATERIAL_SKIN_LARGE: return new(0.89f, 0.47f, 0.20f);
					 //#declare Tan = color red 0.858824 green 0.576471 blue 0.439216
					 case SKY_HAV_MAT_ORGANIC: return new(0.858824f, 0.576471f, 0.439216f);
					 //#declare Thistle = color red 0.847059 green 0.74902 blue 0.847059
					 case SKY_HAV_MAT_MATERIAL_BONE: return new(0.847059f, 0.74902f, 0.847059f);
					 //#declare LightWood = color red 0.91 green 0.76 blue 0.65
					 case SKY_HAV_MAT_HEAVY_WOOD: return new(0.91f, 0.76f, 0.65f);
					 //#declare NeonBlue = color red 0.30 green 0.30 blue 1.00
					 case SKY_HAV_MAT_MATERIAL_CHAIN: return new(0.30f, 0.30f, 1.00f);
					 //#declare IndianRed = color red 0.309804 green 0.184314 blue 0.184314
					 case SKY_HAV_MAT_DIRT: return new(0.309804f, 0.184314f, 0.184314f);
					 //#declare MediumBlue = color red 0.196078 green 0.196078 blue 0.8
					 case SKY_HAV_MAT_MATERIAL_ARMOR_LIGHT: return new(0.196078f, 0.196078f, 0.8f);
					 //#declare MediumForestGreen = color red 0.419608 green 0.556863 blue 0.137255
					 case SKY_HAV_MAT_MATERIAL_SHIELD_LIGHT: return new(0.419608f, 0.556863f, 0.137255f);
					 //#declare OldGold = color red 0.81 green 0.71 blue 0.23
					 case SKY_HAV_MAT_MATERIAL_COIN: return new(0.81f, 0.71f, 0.23f);
					 //#declare MediumGoldenrod = color red 0.917647 green 0.917647 blue 0.678431
					 case SKY_HAV_MAT_MATERIAL_SHIELD_HEAVY: return new(0.917647f, 0.917647f, 0.678431f);
					 //#declare MediumOrchid = color red 0.576471 green 0.439216 blue 0.858824
					 case SKY_HAV_MAT_MATERIAL_ARMOR_HEAVY: return new(0.576471f, 0.439216f, 0.858824f);
					 //#declare MediumSeaGreen = color red 0.258824 green 0.435294 blue 0.258824
					 case SKY_HAV_MAT_MATERIAL_ARROW: return new(0.258824f, 0.435294f, 0.258824f);
					 //#declare RichBlue = color red 0.35 green 0.35 blue 0.67
					 case SKY_HAV_MAT_GLASS: return new(0.35f, 0.35f, 0.67f);
					 //#declare Firebrick = color red 0.556863 green 0.137255 blue 0.137255
					 case SKY_HAV_MAT_STONE: return new(0.556863f, 0.137255f, 0.137255f);
					 //#declare DarkGreen = color red 0.184314 green 0.309804 blue 0.184314
					 case SKY_HAV_MAT_CLOTH: return new(0.184314f, 0.309804f, 0.184314f);
					 //#declare MidnightBlue = color red 0.184314 green 0.184314 blue 0.309804
					 case SKY_HAV_MAT_MATERIAL_BLUNT_2HAND: return new(0.184314f, 0.184314f, 0.309804f);
					 //#declare VioletRed = color red 0.8 green 0.196078 blue 0.6
					 case SKY_HAV_MAT_UNKNOWN_4239621792: return new(0.8f, 0.196078f, 0.6f);
					 //#declare Salmon = color red 0.435294 green 0.258824 blue 0.258824
					 case SKY_HAV_MAT_MATERIAL_BOULDER_MEDIUM: return new(0.435294f, 0.258824f, 0.258824f);
				}
				//red
				return new(1.0f, 0.0f, 0.0f);
		  }
	 }
}
