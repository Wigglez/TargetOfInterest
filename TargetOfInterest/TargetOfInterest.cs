// Behavior originally contributed by mastahg Heavily modified by mjj23, rewritten by Wigglez

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using Styx.Common;
using Styx.CommonBot;
using Styx.CommonBot.Profiles;
using Styx.CommonBot.Routines;
using Styx.TreeSharp;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
using Action = Styx.TreeSharp.Action;

// ReSharper disable once CheckNamespace
namespace Styx.Bot.Quest_Behaviors {
    [CustomBehaviorFileName(@"Misc\TargetOfInterest")]
    public class TargetOfInterest : CustomForcedBehavior {
        // ===========================================================
        // Constants
        // ===========================================================
        // ===========================================================
        // Fields
        // ===========================================================

        private bool _isBehaviorDone;
        private bool _isDisposed;
        private Composite _root;

        private WoWUnit _killUnit;
        private WoWUnit _ignoreUnit;

        // ===========================================================
        // Constructors
        // ===========================================================

        /// <summary>
        ///     AuraId: Aura on boss
        ///     BossId: Id of the boss to kill at the end of the fight
        ///     IgnoreBoss: Ignore the boss regardless of auras while priority targets are alive (Default: False)
        ///     BlacklistMobN: List of mobs to blacklist during the fight from 1 to N
        ///     KillOrderN: List of targets in the priority list which will be attacked in order from 1 to N
        /// </summary>
        public TargetOfInterest(Dictionary<string, string> args)
            : base(args) {
            try {
                AuraId = GetAttributeAsNullable("AuraId", false, ConstrainAs.MobId, new[] { "NpcId", "NpcID" }) ?? 0;
                BossId = GetAttributeAsNullable("BossId", true, ConstrainAs.MobId, new[] { "NpcId", "NpcID" }) ?? 0;
                IgnoreBoss = GetAttributeAsNullable<bool>("IgnoreBoss", false, null, null) ?? false;
                BlacklistMob = GetNumberedAttributesAsArray("BlacklistMob", 0, ConstrainAs.MobId, new[] { "NpcId" });
                KillOrder = GetNumberedAttributesAsArray("KillOrder", 0, ConstrainAs.MobId, new[] { "NpcId" });
            } catch(Exception except) {
                LogMessage("error", "BEHAVIOR MAINTENANCE PROBLEM: " + except.Message + "\nFROM HERE:\n" + except.StackTrace + "\n");
                IsAttributeProblem = true;
            }
        }

        // ===========================================================
        // Getter & Setter
        // ===========================================================

        public int AuraId { get; set; }
        public int BossId { get; set; }
        public int[] BlacklistMob { get; set; }
        public bool IgnoreBoss { get; set; }
        public int[] KillOrder { get; set; }

        public static LocalPlayer Me {
            get { return (StyxWoW.Me); }
        }

        public WoWUnit PriorityUnit {
            get {
                foreach(var entry in KillOrder) {
                    _killUnit = ObjectManager.GetObjectsOfTypeFast<WoWUnit>().Where(u => u.Entry == entry && u.IsAlive).OrderBy(u => u.Distance).FirstOrDefault();

                    if(_killUnit != null) {
                        return _killUnit;
                    }
                }

                return null;
            }
        }

        public WoWUnit BlacklistUnit {
            get {
                foreach(var entry in BlacklistMob) {
                    _ignoreUnit = ObjectManager.GetObjectsOfTypeFast<WoWUnit>().Where(u => !Blacklist.Contains((ulong)entry, BlacklistFlags.Combat) && u.IsAlive && u.Entry != BossId).OrderBy(u => u.Distance).FirstOrDefault();

                    if(_ignoreUnit != null) {
                        return _ignoreUnit;
                    }
                }

                return null;
            }
        }

        public WoWUnit Boss {
            get {
                return (ObjectManager.GetObjectsOfTypeFast<WoWUnit>().FirstOrDefault(u => u.Entry == BossId));
            }
        }

        // ===========================================================
        // Methods for/from SuperClass/Interfaces
        // ===========================================================

        public override bool IsDone {
            get { return (_isBehaviorDone); }
        }

        public override void OnStart() {
            OnStart_HandleAttributeProblem();

            if(IsDone) {
                return;
            }

            if(TreeRoot.Current == null || TreeRoot.Current.Root == null ||
                TreeRoot.Current.Root.LastStatus == RunStatus.Running) {
                return;
            }

            var currentRoot = TreeRoot.Current.Root;

            if(!(currentRoot is GroupComposite)) {
                return;
            }

            var root = (GroupComposite)currentRoot;
            root.InsertChild(0, CreateBehavior());

            BotEvents.OnBotStopped += BotEvents_OnBotStop;
        }

        public override void OnFinished() {
            if(!_isDisposed) {
                BotEvents.OnBotStopped -= BotEvents_OnBotStop;

                _isBehaviorDone = false;

                TreeRoot.GoalText = string.Empty;
                TreeRoot.StatusText = string.Empty;

                GC.SuppressFinalize(this);

                base.OnFinished();
            }

            _isDisposed = true;
        }

        protected override Composite CreateBehavior() {
            return _root ?? (_root =
                new Decorator(ret => !_isBehaviorDone,
                    new PrioritySelector(
                        DoneYet,
                        BlackListAdds,
                        PriorityKill,
                        RemoveBlackListAdds,
                        KillBoss
                    )
                )
            );
        }

        // ===========================================================
        // Methods
        // ===========================================================

        public void CustomNormalLog(string message, params object[] args) {
            Logging.Write(Colors.DeepSkyBlue, "[TargetOfInterest]: " + message, args);
        }

        public Composite DoneYet {
            get {
                return new Decorator(ret => Boss.IsDead || Boss == null,
                    new Action(delegate {
                    CustomNormalLog("Behavior finished.");
                    _isBehaviorDone = true;
                    return RunStatus.Success;
                }));
            }
        }

        public Composite UseCombatRoutine {
            get {
                return new PrioritySelector(
                    RoutineManager.Current.HealBehavior,
                    RoutineManager.Current.CombatBuffBehavior,
                    RoutineManager.Current.CombatBehavior
                );
            }
        }

        public Composite BlackListAdds {
            get {
                return new Decorator(r => !Blacklist.Contains(BlacklistUnit, BlacklistFlags.Combat) && BlacklistMob.Any(),
                    new Action(r => Blacklist.Add(BlacklistUnit, BlacklistFlags.Combat, TimeSpan.FromMinutes(5)))
                );
            }
        }

        public Composite RemoveBlackListAdds {
            get {
                return new Decorator(r => BlacklistUnit != null && Blacklist.Contains(BlacklistUnit, BlacklistFlags.Combat) && PriorityUnit == null,
                        new Action(r => Blacklist.Flush())
                );
            }
        }


        public Composite PriorityKill {
            get {
                return new Decorator(r => PriorityUnit != null && Boss.HasAura(AuraId) || IgnoreBoss,
                    new PrioritySelector(
                        new Decorator(r => Me.CurrentTarget != PriorityUnit || Me.CurrentTarget.IsDead,
                            new Action(r => PriorityUnit.Target())
                        ),

                        UseCombatRoutine
                    )
                );
            }
        }


        public Composite KillBoss {
            get {
                return new Decorator(r => Boss != null && !Boss.HasAura(AuraId),
                    new PrioritySelector(
                        new Decorator(r => Me.CurrentTarget != Boss,
                            new Action(r => Boss.Target())
                        ),

                        UseCombatRoutine
                    )
                );
            }
        }

        // ===========================================================
        // Inner and Anonymous Classes
        // ===========================================================

        private void BotEvents_OnBotStop(EventArgs args) {
            OnFinished();
        }
    }
}