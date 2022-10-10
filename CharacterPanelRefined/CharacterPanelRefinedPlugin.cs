﻿using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Dalamud;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Logging;
using Dalamud.Memory;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.STD;

namespace CharacterPanelRefined;

public class CharacterPanelRefinedPlugin : IDalamudPlugin {
    public string Name => "Character Panel Refined";
    
    public Configuration Configuration { get; }
    public ConfigWindow ConfigWindow { get; }

    private readonly Hook<AddonOnSetup> characterStatusOnSetup;
    private readonly Hook<RequestUpdate> characterStatusRequestUpdate;
    private readonly Tooltips tooltips;

    private unsafe delegate void* AddonOnSetup(AtkUnitBase* atkUnitBase, int a2, void* a3);

    private unsafe delegate void* RequestUpdate(AtkUnitBase* atkUnitBase, void* a2);


    private IntPtr characterStatusPtr;
    private unsafe AtkTextNode* dhChancePtr;
    private unsafe AtkTextNode* dhDamagePtr;
    private unsafe AtkTextNode* detDmgIncreasePtr;
    private unsafe AtkTextNode* critDmgPtr;
    private unsafe AtkTextNode* critChancePtr;
    private unsafe AtkTextNode* critDmgIncreasePtr;
    private unsafe AtkTextNode* physMitPtr;
    private unsafe AtkTextNode* magicMitPtr;
    private unsafe AtkTextNode* sksSpeedIncreasePtr;
    private unsafe AtkTextNode* sksGcdPtr;
    private unsafe AtkTextNode* spsSpeedIncreasePtr;
    private unsafe AtkTextNode* spsGcdPtr;
    private unsafe AtkTextNode* sps28GcdPtr;
    private unsafe AtkTextNode* tenMitPtr;
    private unsafe AtkTextNode* pieManaPtr;
    private unsafe AtkTextNode* expectedDamagePtr;
    private unsafe AtkTextNode* expectedHealPtr;
    private unsafe AtkResNode* attributesPtr;
    private unsafe AtkResNode* offensivePtr;
    private unsafe AtkResNode* defensivePtr;
    private unsafe AtkResNode* physPropertiesPtr;
    private unsafe AtkResNode* pietyPtr;
    private unsafe AtkResNode* tenacityPtr;
    private unsafe AtkResNode* spellSpeedPtr;
    private unsafe AtkResNode* skillSpeedPtr;
    private JobId lastJob;

    public CharacterPanelRefinedPlugin(DalamudPluginInterface pluginInterface) {
        
        Service.Initialize(pluginInterface);

        Configuration = Configuration.Get(pluginInterface);
        ConfigWindow = new ConfigWindow(this);
        tooltips = new Tooltips();
        
        UpdateLanguage();

        var characterStatusOnSetupPtr =
            Service.SigScanner.ScanText("4C 8B DC 55 53 41 56 49 8D 6B A1 48 81 EC F0 00 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 07");
        var characterStatusRequestUpdatePtr = Service.SigScanner.ScanText(
            "48 89 5c 24 08 48 89 6c 24 10 48 89 74 24 18 57 48 83 ec 50 48 8b 7a 18 48 8b f1 48 8b aa ?? ?? ?? ?? 48 8b 47 20");

        unsafe {
            characterStatusOnSetup = Hook<AddonOnSetup>.FromAddress(characterStatusOnSetupPtr, CharacterStatusOnSetup);
            characterStatusRequestUpdate = Hook<RequestUpdate>.FromAddress(characterStatusRequestUpdatePtr, CharacterStatusRequestUpdate);
        }

        characterStatusOnSetup.Enable();
        characterStatusRequestUpdate.Enable();
        
        pluginInterface.UiBuilder.Draw += ConfigWindow.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += () => ConfigWindow.ShowConfig = true;
        Service.CommandManager.AddHandler("/cprconfig",
            new CommandInfo((_, _) => ConfigWindow.ShowConfig ^= true) { HelpMessage = "Open the Character Panel Refined configuration." });
    }
    
    public void UpdateLanguage() {
        var lang = "";
        
        if (Configuration.UseGameLanguage)
        {
            lang = Service.ClientState.ClientLanguage switch {
                ClientLanguage.English => "",
                ClientLanguage.French => "fr",
                ClientLanguage.German => "de",
                ClientLanguage.Japanese => "ja",
                _ => ""
            };
        }
        Localization.Culture = new CultureInfo(lang);
        
        tooltips.Reload();
    }

    private unsafe void* CharacterStatusRequestUpdate(AtkUnitBase* atkUnitBase, void* a2) {
        try {
            if ((IntPtr)atkUnitBase == characterStatusPtr) {
                var uiState = UIState.Instance();
                var lvl = uiState->PlayerState.CurrentLevel;
                var levelModifier = LevelModifiers.LevelTable[lvl];
                var statInfo = new StatInfo();

                var dh = Equations.CalcDh(uiState->PlayerState.Attributes[(int)Attributes.DirectHit], ref statInfo, ref levelModifier);
                dhChancePtr->SetText($"{statInfo.DisplayValue:P1}");
                if (dhDamagePtr != null)
                    dhDamagePtr->SetText($"{statInfo.DisplayValue * 0.25:P1}");
                tooltips.Update(Tooltips.Entry.DirectHit, ref statInfo);

                var det = Equations.CalcDet(uiState->PlayerState.Attributes[(int)Attributes.Determination], ref statInfo, ref levelModifier);
                tooltips.Update(Tooltips.Entry.Determination, ref statInfo);
                detDmgIncreasePtr->SetText($"{statInfo.DisplayValue:P1}");

                var critRate = Equations.CalcCritRate(uiState->PlayerState.Attributes[(int)Attributes.CriticalHit], ref statInfo, ref levelModifier);
                tooltips.Update(Tooltips.Entry.Crit, ref statInfo);
                critChancePtr->SetText($"{statInfo.DisplayValue:P1}");

                var critDmg = Equations.CalcCritDmg(uiState->PlayerState.Attributes[(int)Attributes.CriticalHit], ref statInfo, ref levelModifier);
                critDmgPtr->SetText($"{statInfo.DisplayValue:P1}");
                if (critDmgIncreasePtr != null)
                    critDmgIncreasePtr->SetText($"{critRate * (critDmg-1):P1}");

                Equations.CalcMagicDef(uiState->PlayerState.Attributes[(int)Attributes.MagicDefense], ref statInfo, ref levelModifier);
                magicMitPtr->SetText($"{statInfo.DisplayValue:P0}");
                tooltips.Update(Tooltips.Entry.MagicDefense, ref statInfo);

                Equations.CalcDef(uiState->PlayerState.Attributes[(int)Attributes.Defense], ref statInfo, ref levelModifier);
                physMitPtr->SetText($"{statInfo.DisplayValue:P0}");
                tooltips.Update(Tooltips.Entry.Defense, ref statInfo);

                var jobId = (JobId)(*((byte*)&uiState->PlayerState + 0x6E));

                StatInfo gcd25 = new(), gcd28 = new();
                if (jobId.IsCaster()) {
                    var isBlm = jobId == JobId.BLM;
                    Equations.CalcSpeed(uiState->PlayerState.Attributes[(int)Attributes.SpellSpeed], ref statInfo, ref gcd25, ref gcd28, ref levelModifier, isBlm);
                    spsSpeedIncreasePtr->SetText($"{statInfo.DisplayValue:P1}");
                    spsGcdPtr->SetText($"{gcd25.DisplayValue:N2}s");
                    sps28GcdPtr->SetText($"{gcd28.DisplayValue:N2}s");
                    tooltips.UpdateSpeed(ref statInfo, ref gcd25, ref gcd28, isBlm);
                    SetTooltip(spsSpeedIncreasePtr, isBlm ? Tooltips.Entry.Speed28 : Tooltips.Entry.Speed);
                } else {
                    Equations.CalcSpeed(uiState->PlayerState.Attributes[(int)Attributes.SkillSpeed], ref statInfo, ref gcd25, ref gcd28, ref levelModifier, false);
                    sksSpeedIncreasePtr->SetText($"{statInfo.DisplayValue:P1}");
                    sksGcdPtr->SetText($"{gcd25.DisplayValue:N2}s");
                    tooltips.UpdateSpeed(ref statInfo, ref gcd25, ref gcd28, false);
                }

                Equations.CalcPiety(uiState->PlayerState.Attributes[(int)Attributes.Piety], ref statInfo, ref levelModifier);
                pieManaPtr->SetText($"{statInfo.DisplayValue:N0}");
                tooltips.Update(Tooltips.Entry.Piety, ref statInfo);

                var ten = Equations.CalcTenacity(uiState->PlayerState.Attributes[(int)Attributes.Tenacity], ref statInfo, ref levelModifier);
                tenMitPtr->SetText($"{statInfo.DisplayValue:P1}");
                tooltips.Update(Tooltips.Entry.Tenacity, ref statInfo);
                
                Equations.CalcHp(uiState, jobId, out var hpPerVitality, out var hpModifier);
                tooltips.UpdateVitality(jobId.ToString(), hpPerVitality, hpModifier);

                if (expectedDamagePtr != null || expectedHealPtr != null) {
                    var (avgDamage, normalDamage, critDamage, avgHeal, normalHeal, critHeal) =
                        Equations.CalcExpectedOutput(uiState, jobId, det, critDmg, critRate, dh, ten, ref levelModifier);
                    if (expectedDamagePtr != null) {
                        expectedDamagePtr->SetText($"{avgDamage:N0}");
                        tooltips.UpdateExpectedOutput(Tooltips.Entry.ExpectedDamage, normalDamage, critDamage);
                    }
                    if (expectedHealPtr != null) {
                        expectedHealPtr->SetText($"{avgHeal:N0}");
                        tooltips.UpdateExpectedOutput(Tooltips.Entry.ExpectedHeal, normalHeal, critHeal);
                    }
                }

                if (jobId != lastJob) {
                    UpdateCharacterPanelForJob(jobId);
                }
            } else {
                ClearPointers();
            }
        } catch (Exception e) {
            PluginLog.LogError(e, "Failed to update CharacterStatus");
        }

        return characterStatusRequestUpdate.Original(atkUnitBase, a2);
    }

    private unsafe void ClearPointers() {
        characterStatusPtr = IntPtr.Zero;
        dhChancePtr = null;
        dhDamagePtr = null;
        detDmgIncreasePtr = null;
        critDmgPtr = null;
        critChancePtr = null;
        critDmgIncreasePtr = null;
        physMitPtr = null;
        magicMitPtr = null;
        sksSpeedIncreasePtr = null;
        sksGcdPtr = null;
        spsSpeedIncreasePtr = null;
        spsGcdPtr = null;
        sps28GcdPtr = null;
        tenMitPtr = null;
        pieManaPtr = null;
        expectedDamagePtr = null;
        expectedHealPtr = null;
        attributesPtr = null;
        offensivePtr = null;
        defensivePtr = null;
        physPropertiesPtr = null;
        pietyPtr = null;
        tenacityPtr = null;
        spellSpeedPtr = null;
        skillSpeedPtr = null;
    }

    private unsafe void UpdateCharacterPanelForJob(JobId job) {
        if (job.IsCrafter() || job.IsGatherer()) {
            attributesPtr->ChildNode->ToggleVisibility(false);
            attributesPtr->ChildNode->PrevSiblingNode->ToggleVisibility(false);
            attributesPtr->ChildNode->PrevSiblingNode->PrevSiblingNode->PrevSiblingNode->ToggleVisibility(false);
            attributesPtr->ChildNode->PrevSiblingNode->PrevSiblingNode->PrevSiblingNode->PrevSiblingNode->ToggleVisibility(false);
            offensivePtr->ToggleVisibility(false);
            defensivePtr->ToggleVisibility(false);
            physPropertiesPtr->ToggleVisibility(false);
            if (expectedDamagePtr != null)
                expectedDamagePtr->AtkResNode.ParentNode->ToggleVisibility(false);
            if (expectedHealPtr != null)
                expectedHealPtr->AtkResNode.ParentNode->ToggleVisibility(false);
        } else {
            offensivePtr->ToggleVisibility(true);
            defensivePtr->ToggleVisibility(true);
            physPropertiesPtr->ToggleVisibility(true);
            if (expectedDamagePtr != null)
                expectedDamagePtr->AtkResNode.ParentNode->ToggleVisibility(true);
            if (expectedHealPtr != null)
                expectedHealPtr->AtkResNode.ParentNode->ToggleVisibility(true);
            if (job.IsCaster()) {
                skillSpeedPtr->ToggleVisibility(false);
                spellSpeedPtr->ToggleVisibility(true);
                skillSpeedPtr->ToggleVisibility(false);
                spellSpeedPtr->ToggleVisibility(true);
                sps28GcdPtr->AtkResNode.ToggleVisibility(job == JobId.BLM);
                sps28GcdPtr->AtkResNode.PrevSiblingNode->ToggleVisibility(job == JobId.BLM);
                if (job.UsesMind()) {
                    attributesPtr->ChildNode->ToggleVisibility(true);
                    attributesPtr->ChildNode->PrevSiblingNode->ToggleVisibility(false);
                    attributesPtr->ChildNode->PrevSiblingNode->PrevSiblingNode->PrevSiblingNode->ToggleVisibility(false);
                    attributesPtr->ChildNode->PrevSiblingNode->PrevSiblingNode->PrevSiblingNode->PrevSiblingNode->ToggleVisibility(false);
                    pietyPtr->ToggleVisibility(true);
                    tenacityPtr->ToggleVisibility(false);
                } else {
                    attributesPtr->ChildNode->ToggleVisibility(false);
                    attributesPtr->ChildNode->PrevSiblingNode->ToggleVisibility(true);
                    attributesPtr->ChildNode->PrevSiblingNode->PrevSiblingNode->PrevSiblingNode->ToggleVisibility(false);
                    attributesPtr->ChildNode->PrevSiblingNode->PrevSiblingNode->PrevSiblingNode->PrevSiblingNode->ToggleVisibility(false);
                    pietyPtr->ToggleVisibility(false);
                    tenacityPtr->ToggleVisibility(false);
                }
            } else {
                skillSpeedPtr->ToggleVisibility(true);
                spellSpeedPtr->ToggleVisibility(false);
                if (job.UsesDexterity()) {
                    attributesPtr->ChildNode->ToggleVisibility(false);
                    attributesPtr->ChildNode->PrevSiblingNode->ToggleVisibility(false);
                    attributesPtr->ChildNode->PrevSiblingNode->PrevSiblingNode->PrevSiblingNode->ToggleVisibility(true);
                    attributesPtr->ChildNode->PrevSiblingNode->PrevSiblingNode->PrevSiblingNode->PrevSiblingNode->ToggleVisibility(false);
                    pietyPtr->ToggleVisibility(false);
                    tenacityPtr->ToggleVisibility(false);
                } else {
                    attributesPtr->ChildNode->ToggleVisibility(false);
                    attributesPtr->ChildNode->PrevSiblingNode->ToggleVisibility(false);
                    attributesPtr->ChildNode->PrevSiblingNode->PrevSiblingNode->PrevSiblingNode->ToggleVisibility(false);
                    attributesPtr->ChildNode->PrevSiblingNode->PrevSiblingNode->PrevSiblingNode->PrevSiblingNode->ToggleVisibility(true);
                    pietyPtr->ToggleVisibility(false);
                    tenacityPtr->ToggleVisibility(job.UsesTenacity());
                }
            }
        }

        lastJob = job;
    }

    private unsafe void SetTooltip(AtkComponentNode* parentNode, Tooltips.Entry entry) {
        if (!Configuration.ShowTooltips)
            return;
        if (parentNode == null)
            return;
        var collisionNode = parentNode->Component->UldManager.RootNode;
        if (collisionNode == null)
            return;

        var ttMgr = AtkStage.GetSingleton()->TooltipManager;
        var ttMsg = Find(ttMgr.TooltipMap.Head->Parent, collisionNode).Value;
        ttMsg->AtkTooltipArgs.Text = (byte*)tooltips[entry];
    }

    private unsafe void SetTooltip(AtkTextNode* node, Tooltips.Entry entry) {
        SetTooltip((AtkComponentNode*)node->AtkResNode.ParentNode, entry);
    }

    // Traverse a std::map
    private unsafe TVal Find<TKey, TVal>(StdMap<Pointer<TKey>, TVal>.Node* node, TKey* item) where TKey : unmanaged where TVal : unmanaged {
        while (!node->IsNil) {
            if (node->KeyValuePair.Item1.Value < item) {
                node = node->Right;
                continue;
            }

            if (node->KeyValuePair.Item1.Value > item) {
                node = node->Left;
                continue;
            }

            return node->KeyValuePair.Item2;
        }

        return default;
    }
    
    

    private unsafe void* CharacterStatusOnSetup(AtkUnitBase* atkUnitBase, int a2, void* a3) {
        var val = characterStatusOnSetup.Original(atkUnitBase, a2, a3);

        try {
            var job = (JobId)UIState.Instance()->PlayerState.CurrentClassJobId;

            attributesPtr = atkUnitBase->UldManager.SearchNodeById(26);
            var mndNode = attributesPtr->ChildNode;
            mndNode->X = 10;
            SetTooltip((AtkComponentNode*)mndNode, Tooltips.Entry.MainStat);
            mndNode->Y = 40;
            var intNode = mndNode->PrevSiblingNode;
            intNode->X = 10;
            intNode->Y = 40;
            SetTooltip((AtkComponentNode*)intNode, Tooltips.Entry.MainStat);

            var vitalityNode = intNode->PrevSiblingNode;
            vitalityNode->Y = 20;
            SetTooltip((AtkComponentNode*)vitalityNode, Tooltips.Entry.Vitality);

            var attributesHeight = 130;
            
            var gearProp = atkUnitBase->UldManager.SearchNodeById(80);
            if (Configuration.ShowAvgDamage) {
                attributesHeight += 20;
                gearProp->Y = 100;
                var avgItemLvlNode = gearProp->ChildNode;
                avgItemLvlNode->PrevSiblingNode->ToggleVisibility(false); // header
                expectedDamagePtr = AddStatRow((AtkComponentNode*)avgItemLvlNode, Localization.Panel_Damage_per_100_Potency, true);
                SetTooltip(expectedDamagePtr, Tooltips.Entry.ExpectedDamage);
            } else {
                gearProp->ToggleVisibility(false);
            }
            
            var mentProperties = atkUnitBase->UldManager.SearchNodeById(58);
            var magAtkPotency = mentProperties->ChildNode->PrevSiblingNode;
            if (Configuration.ShowAvgHealing) {
                attributesHeight += 20;
                magAtkPotency->Y = -160;
                expectedHealPtr = AddStatRow((AtkComponentNode*)magAtkPotency, Localization.Panel_Heal_per_100_Potency, true);
                SetTooltip(expectedHealPtr, Tooltips.Entry.ExpectedHeal);
            } else {
                magAtkPotency->ToggleVisibility(false);
            }

            var dexNode = vitalityNode->PrevSiblingNode;
            dexNode->Y = 40;
            SetTooltip((AtkComponentNode*)dexNode, Tooltips.Entry.MainStat);
            var strNode = dexNode->PrevSiblingNode;
            strNode->Y = 40;
            SetTooltip((AtkComponentNode*)strNode, Tooltips.Entry.MainStat);

            var offensiveHeight = 130;
            
            offensivePtr = atkUnitBase->UldManager.SearchNodeById(36);
            offensivePtr->Y = attributesHeight;
            var dh = offensivePtr->ChildNode;
            dh->Y = 120;
            dhChancePtr = AddStatRow((AtkComponentNode*)dh, Localization.Panel_Direct_Hit_Chance);
            if (Configuration.ShowDhDamageIncrease) {
                offensiveHeight += 20;
                magAtkPotency->Y -= 20;
                dhDamagePtr = AddStatRow((AtkComponentNode*)dh, Localization.Panel_Damage_Increase);
            }
            SetTooltip(dhChancePtr, Tooltips.Entry.DirectHit);
            var det = dh->PrevSiblingNode;
            det->Y = 80;
            detDmgIncreasePtr = AddStatRow((AtkComponentNode*)det, Localization.Panel_Damage_Increase);
            SetTooltip(detDmgIncreasePtr, Tooltips.Entry.Determination);
            var crit = det->PrevSiblingNode;
            critChancePtr = AddStatRow((AtkComponentNode*)crit, Localization.Panel_Crit_Chance);
            critDmgPtr = AddStatRow((AtkComponentNode*)crit, Localization.Panel_Crit_Damage);
            if (Configuration.ShowCritDamageIncrease) {
                offensiveHeight += 20;
                dh->Y += 20;
                det->Y += 20;
                magAtkPotency->Y -= 20;
                critDmgIncreasePtr = AddStatRow((AtkComponentNode*)crit, Localization.Panel_Damage_Increase);
            }
            SetTooltip(critChancePtr, Tooltips.Entry.Crit);

            defensivePtr = atkUnitBase->UldManager.SearchNodeById(44);
            defensivePtr->Y = attributesHeight;
            var magicDef = defensivePtr->ChildNode;
            magicDef->Y = 60;
            magicMitPtr = AddStatRow((AtkComponentNode*)magicDef, Localization.Panel_Magic_Mitigation);
            SetTooltip(magicMitPtr, Tooltips.Entry.MagicDefense);
            var def = magicDef->PrevSiblingNode;
            physMitPtr = AddStatRow((AtkComponentNode*)def, Localization.Panel_Physical_Mitigation);
            SetTooltip(physMitPtr, Tooltips.Entry.Defense);

            mentProperties->X = 0;
            mentProperties->Y = attributesHeight + offensiveHeight;
            spellSpeedPtr = mentProperties->ChildNode;
            magAtkPotency->PrevSiblingNode->ToggleVisibility(false); // Magic Heal Potency
            magAtkPotency->PrevSiblingNode->PrevSiblingNode->ToggleVisibility(false); // Header
            spsSpeedIncreasePtr = AddStatRow((AtkComponentNode*)spellSpeedPtr, Localization.Panel_Skill_Speed_Increase);
            spsGcdPtr = AddStatRow((AtkComponentNode*)spellSpeedPtr, Localization.Panel_GCD);
            sps28GcdPtr = AddStatRow((AtkComponentNode*)spellSpeedPtr, Localization.Panel_Fire_IV_GCD);
            SetTooltip(spsSpeedIncreasePtr, Tooltips.Entry.Speed);

            physPropertiesPtr = atkUnitBase->UldManager.SearchNodeById(51);
            physPropertiesPtr->Y = attributesHeight + offensiveHeight + 40;
            skillSpeedPtr = physPropertiesPtr->ChildNode;
            skillSpeedPtr->Y = 20;
            skillSpeedPtr->PrevSiblingNode->ToggleVisibility(false); // Attack Power
            ((AtkTextNode*)skillSpeedPtr->PrevSiblingNode->PrevSiblingNode->ChildNode->PrevSiblingNode)->SetText(Localization.Panel_Speed_Properties);
            sksSpeedIncreasePtr = AddStatRow((AtkComponentNode*)skillSpeedPtr, Localization.Panel_Skill_Speed_Increase);
            sksGcdPtr = AddStatRow((AtkComponentNode*)skillSpeedPtr, Localization.Panel_GCD);
            SetTooltip(sksSpeedIncreasePtr, Tooltips.Entry.Speed);

            var roleProp = atkUnitBase->UldManager.SearchNodeById(86);
            roleProp->Y = 60;
            pietyPtr = roleProp->ChildNode;
            pietyPtr->Y = 20;
            pieManaPtr = AddStatRow((AtkComponentNode*)pietyPtr, Localization.Panel_Mana_per_Tick);
            SetTooltip(pieManaPtr, Tooltips.Entry.Piety);
            tenacityPtr = pietyPtr->PrevSiblingNode;
            tenMitPtr = AddStatRow((AtkComponentNode*)tenacityPtr, Localization.Panel_Damage_and_Mitigation);
            tenacityPtr->PrevSiblingNode->ToggleVisibility(false); // header
            SetTooltip(tenMitPtr, Tooltips.Entry.Tenacity);

            var craftingPtr = atkUnitBase->UldManager.SearchNodeById(73);
            craftingPtr->X = 0;
            craftingPtr->Y = 80;
            craftingPtr->ChildNode->PrevSiblingNode->PrevSiblingNode->ToggleVisibility(false); // header

            var gatheringPtr = atkUnitBase->UldManager.SearchNodeById(66);
            gatheringPtr->X = 0;
            gatheringPtr->Y = 80;
            gatheringPtr->ChildNode->PrevSiblingNode->PrevSiblingNode->ToggleVisibility(false); // header

            characterStatusPtr = (IntPtr)atkUnitBase;

            UpdateCharacterPanelForJob(job);
        } catch (Exception e) {
            PluginLog.LogError(e, "Failed to modify character");
        }

        return val;
    }

    private unsafe AtkTextNode* AddStatRow(AtkComponentNode* parentNode, string label, bool hideOriginal = false) {
        ExpandNodeList(parentNode, 2);
        var collisionNode = parentNode->Component->UldManager.RootNode;
        if (!hideOriginal) {
            parentNode->AtkResNode.Height += 20;
            collisionNode->Height += 20;
        }
        var numberNode = (AtkTextNode*)collisionNode->PrevSiblingNode;
        var labelNode = (AtkTextNode*)numberNode->AtkResNode.PrevSiblingNode;
        var newNumberNode = CloneNode(numberNode);
        var prevSiblingNode = labelNode->AtkResNode.PrevSiblingNode;
        labelNode->AtkResNode.PrevSiblingNode = (AtkResNode*)newNumberNode;
        newNumberNode->AtkResNode.ParentNode = (AtkResNode*)parentNode;
        newNumberNode->AtkResNode.NextSiblingNode = (AtkResNode*)labelNode;
        newNumberNode->AtkResNode.Y = parentNode->AtkResNode.Height - 24;
        newNumberNode->TextColor = new ByteColor { A = 0xFF, R = 0xA0, G = 0xA0, B = 0xA0 };
        newNumberNode->NodeText.StringPtr = (byte*)MemoryHelper.GameAllocateUi((ulong)newNumberNode->NodeText.BufSize);
        parentNode->Component->UldManager.NodeList[parentNode->Component->UldManager.NodeListCount++] = (AtkResNode*)newNumberNode;
        var newLabelNode = CloneNode(labelNode);
        newNumberNode->AtkResNode.PrevSiblingNode = (AtkResNode*)newLabelNode;
        newLabelNode->AtkResNode.ParentNode = (AtkResNode*)parentNode;
        newLabelNode->AtkResNode.PrevSiblingNode = prevSiblingNode;
        newLabelNode->AtkResNode.NextSiblingNode = (AtkResNode*)newNumberNode;
        newLabelNode->AtkResNode.Y = parentNode->AtkResNode.Height - 24;
        newLabelNode->TextColor = new ByteColor { A = 0xFF, R = 0xA0, G = 0xA0, B = 0xA0 };
        newLabelNode->NodeText.StringPtr = (byte*)MemoryHelper.GameAllocateUi((ulong)newLabelNode->NodeText.BufSize);
        newLabelNode->SetText(label);
        parentNode->Component->UldManager.NodeList[parentNode->Component->UldManager.NodeListCount++] = (AtkResNode*)newLabelNode;
        if (hideOriginal) {
            labelNode->AtkResNode.ToggleVisibility(false);
            numberNode->TextColor.A = 0; // toggle visibility doesn't work since it's constantly updated by the game
        }
        return newNumberNode;
    }

    private static unsafe AtkTextNode* CloneNode(AtkTextNode* original) {
        var size = sizeof(AtkTextNode);
        var allocation = MemoryHelper.GameAllocateUi((ulong)size);
        var bytes = new byte[size];
        Marshal.Copy(new IntPtr(original), bytes, 0, bytes.Length);
        Marshal.Copy(bytes, 0, allocation, bytes.Length);

        var newNode = (AtkResNode*)allocation;
        newNode->ParentNode = null;
        newNode->ChildNode = null;
        newNode->ChildCount = 0;
        newNode->PrevSiblingNode = null;
        newNode->NextSiblingNode = null;
        return (AtkTextNode*)newNode;
    }

    private unsafe void ExpandNodeList(AtkComponentNode* componentNode, ushort addSize) {
        var originalList = componentNode->Component->UldManager.NodeList;
        var originalSize = componentNode->Component->UldManager.NodeListCount;
        var newSize = (ushort)(componentNode->Component->UldManager.NodeListCount + addSize);
        var oldListPtr = new IntPtr(originalList);
        var newListPtr = MemoryHelper.GameAllocateUi((ulong)((newSize + 1) * 8));
        var clone = new IntPtr[originalSize];
        Marshal.Copy(oldListPtr, clone, 0, originalSize);
        Marshal.Copy(clone, 0, newListPtr, originalSize);
        componentNode->Component->UldManager.NodeList = (AtkResNode**)newListPtr;
    }

    public void Dispose() {
        ClearPointers();
        Service.CommandManager.RemoveHandler("/cprconfig");
        characterStatusOnSetup.Dispose();
        characterStatusRequestUpdate.Disable();
        tooltips.Dispose();
    }
}