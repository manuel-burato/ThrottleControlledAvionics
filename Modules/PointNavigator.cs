﻿//   PointNavigator.cs
//
//  Author:
//       Allis Tauri <allista@gmail.com>
//
//  Copyright (c) 2015 Allis Tauri
//
// This work is licensed under the Creative Commons Attribution 4.0 International License. 
// To view a copy of this license, visit http://creativecommons.org/licenses/by/4.0/ 
// or send a letter to Creative Commons, PO Box 1866, Mountain View, CA 94042, USA.

using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ThrottleControlledAvionics
{
	public class PointNavigator : TCAModule
	{
		public class Config : ModuleConfig
		{
			new public const string NODE_NAME = "PN";

			[Persistent] public float MinDistance        = 30;
			[Persistent] public float MinTime            = 10;
			[Persistent] public float OnPathMinDistance  = 300;
			[Persistent] public float MinSpeed           = 10;
			[Persistent] public float MaxSpeed           = 300;
			[Persistent] public float DeltaSpeed         = 10f;
			[Persistent] public float DeltaSpeedF        = 0.5f;
			[Persistent] public float FallingSensitivity = 10f;
			[Persistent] public float FallingCorrection  = 3f;
			[Persistent] public float AltErrSensitivity  = 10f;
			[Persistent] public float DistanceF          = 2;
			[Persistent] public float DirectNavThreshold = 1;
			[Persistent] public float GCNavStep          = 0.1f;
			[Persistent] public float LookAheadTime      = 2f;
			[Persistent] public float BearingCutoff      = 60f;
			[Persistent] public float FormationSpeedCutoff = 5f;
			[Persistent] public float FormationFactor    = 0.2f;
			[Persistent] public float FormationBreakTime = 10f;
			[Persistent] public float TakeoffAltitude    = 100f;
			[Persistent] public float BrakeOffset        = 1.5f;

			[Persistent] public PID_Controller DistancePID = new PID_Controller(0.5f, 0f, 0.5f, 0, 100);

			public float BearingCutoffCos;
			public float FormationSpeedSqr;

			public override void Init()
			{
				base.Init();
				DirectNavThreshold *= Mathf.Deg2Rad;
				GCNavStep *= Mathf.Deg2Rad;
				BearingCutoffCos = Mathf.Cos(BearingCutoff*Mathf.Deg2Rad);
				FormationSpeedSqr = FormationSpeedCutoff*FormationSpeedCutoff;
			}
		}
		static Config PN { get { return TCAScenario.Globals.PN; } }
		public PointNavigator(VesselWrapper vsl) { VSL = vsl; }

		float DeltaSpeed;
		readonly PIDf_Controller pid = new PIDf_Controller();
		readonly Timer ArrivedTimer = new Timer();

		SortedList<Guid, ModuleTCA> all_followers = new SortedList<Guid, ModuleTCA>();
		FormationNode fnode;
		Vector3 formation_offset { get { return fnode == null? Vector3.zero : fnode.Offset; } }
		bool CanManeuver = true;
		Timer FormationBreakTimer = new Timer();
		bool keep_formation;

		public override void Init()
		{
			base.Init();
			pid.setPID(PN.DistancePID);
			pid.Reset();
			ArrivedTimer.Period = PN.MinTime;
			FormationBreakTimer.Period = PN.FormationBreakTime;
			CFG.Nav.AddCallback(Navigation.GoToTarget, GoToTarget);
			CFG.Nav.AddCallback(Navigation.FollowTarget, GoToTarget);
			CFG.Nav.AddCallback(Navigation.FollowPath, FollowPath);
			if(CFG.Target != null) SetTarget(CFG.Target);
			#if DEBUG
			RenderingManager.AddToPostDrawQueue(1, RadarBeam);
			#endif
		}

		public override void UpdateState() { IsActive = CFG.Nav && VSL.OnPlanet; }

		public void GoToTarget(bool enable = true)
		{
			if(enable && !VSL.HasTarget) return;
			if(enable) 
			{
				var wp = VSL.vessel.targetObject as WayPoint ?? 
					new WayPoint(VSL.vessel.targetObject);
				start_to(wp);
			}
			else finish();
		}

		public void FollowPath(bool enable = true)
		{
			if(enable && CFG.Waypoints.Count == 0) return;
			if(enable) start_to(CFG.Waypoints.Peek());	
			else finish();
		}

		void start_to(WayPoint wp)
		{
			VSL.UpdateOnPlanetStats();
			if(VSL.LandedOrSplashed) 
			{
				CFG.AltitudeAboveTerrain = true;
				CFG.DesiredAltitude = PN.TakeoffAltitude+VSL.H;
				CFG.VF.OnIfNot(VFlight.AltitudeControl);
			}				
			else VSL.ActionGroups.SetGroup(KSPActionGroup.Gear, false);
			reset_formation();
			SetTarget(wp);
			pid.Reset();
			CFG.HF.On(HFlight.NoseOnCourse);
		}

		void finish()
		{
			SetTarget(null);
			CFG.HF.On(HFlight.Stop);
			reset_formation();
		}

		bool on_arrival()
		{
			if(CFG.Target == null) return false;
			if(CFG.Target.Pause) { PauseMenu.Display(); CFG.Target.Pause = false; }
			if(CFG.Target.Land)	{ CFG.AP.OnIfNot(Autopilot.Land); return true; }
			return false;
		}

		bool update_formation_offset()
		{
			if(updater == null) updater = offset_updater();
			if(updater.MoveNext()) return true;
			updater = null;
			return false;
		}

		void reset_formation()
		{
			VSL.Maneuvering = false;
			VSL.Formation = null;
			fnode = null;			
		}

		IEnumerator updater;
		IEnumerator offset_updater()
		{
			var tvsl = CFG.Target.GetVessel();
			if(tvsl == null) { reset_formation(); CanManeuver = false; yield break; }
			var only_count = false;
			if(tvsl.srf_velocity.sqrMagnitude < PN.FormationSpeedSqr)
			{ reset_formation(); CanManeuver = false; only_count = true; }
			//update followers
			var can_maneuver = true;
			all_followers.Clear();
			for(int i = 0, num_vessels = FlightGlobals.Vessels.Count; i < num_vessels; i++)
			{
				var v = FlightGlobals.Vessels[i];
				if(v == null || v.packed || !v.loaded) continue;
				var tca = ModuleTCA.EnabledTCA(v);
				if(tca != null && 
				   (tca.vessel == VSL.vessel || 
				    tca.CFG.Nav[Navigation.FollowTarget] && 
				    tca.CFG.Target.GetTarget() != null && 
				    tca.CFG.Target.GetTarget() == CFG.Target.GetTarget()))
				{
					all_followers.Add(v.id, tca);
					if(v.id != VSL.vessel.id)
						can_maneuver &= !tca.VSL.Maneuvering || 
							(VSL.Maneuvering && VSL.vessel.id.CompareTo(v.id) > 0);
				}
			}
			if(only_count) yield break;
			CanManeuver = can_maneuver;
			var follower_index = all_followers.IndexOfKey(VSL.vessel.id);
			if(follower_index == 0)
			{
				var forward = tvsl == null? Vector3d.zero : -tvsl.srf_velocity.normalized;
				var side = Vector3d.Cross(VSL.Up, forward).normalized;
				var num_offsets = all_followers.Count+(all_followers.Count%2);
				if(VSL.Formation == null || VSL.Formation.Count != num_offsets)
				{
					VSL.Formation = new List<FormationNode>(num_offsets);
					for(int i = 0; i < num_offsets; i++) 
						VSL.Formation.Add(new FormationNode(tvsl, i, forward, side, PN.MinDistance));
					all_followers.ForEach(p => p.Value.VSL.Formation = VSL.Formation);
				}
				else for(int i = 0; i < num_offsets; i++) 
					VSL.Formation[i].Update(forward, side, PN.MinDistance);
			}
			//			Log("CanManeuver: {0}\nnode {1}\nFormation: {2}", 
			//			    CanManeuver, fnode, VSL.Formation);//debug
			keep_formation = VSL.Formation != null;
			if(VSL.Formation == null || fnode != null) yield break;
			//compute follower offset
			var min_d   = -1f;
			var min_off = 0;
			for(int i = 0; i < VSL.Formation.Count; i++)
			{
				var node = VSL.Formation[i];
				if(node.Follower != null) continue;
				var d = node.Distance(VSL.vessel);
				if(min_d < 0 || min_d > d)
				{
					min_d = d;
					min_off = i;
				}
			}
			VSL.Formation[min_off].Follower = VSL.vessel;
			fnode = VSL.Formation[min_off];
			//			Log("CanManeuver: {0}, node {1}\nFormation:\n{2}", 
			//			    CanManeuver, fnode, VSL.Formation.Aggregate("", (s, f) => s+f+"\n"));//debug
		}

		public void Update()
		{
			if(!IsActive || CFG.Target == null || CFG.Nav.Paused) return;
			CFG.Target.Update(VSL.mainBody);
			//calculate direct distance
			if(CFG.Nav[Navigation.FollowTarget]) update_formation_offset();
			var vdir = Vector3.ProjectOnPlane(CFG.Target.GetTransform().position+formation_offset-VSL.vessel.transform.position, VSL.Up);
			var distance = Utils.ClampL(vdir.magnitude-VSL.R, 0);
			//handle flying in formation
			var tvsl = CFG.Target.GetVessel();
			var tvel = Vector3.zero;
			var vel_is_set = false;
			var end_distance = CFG.Target.Distance;
			var dvel = VSL.HorizontalVelocity;
			if(tvsl != null && tvsl.loaded && CFG.Nav[Navigation.FollowTarget])
			{
				if(formation_offset.IsZero()) end_distance *= all_followers.Count/2f;
				tvel = Vector3d.Exclude(VSL.Up, tvsl.srf_velocity+tvsl.acceleration*PN.LookAheadTime);
				dvel -= tvel;
				var tvel_m = tvel.magnitude;
				var dir2vel_cos = Vector3.Dot(vdir.normalized, tvel.normalized);
				var lat_dir  = Vector3.ProjectOnPlane(vdir-VSL.PredictedHorVelocity(PN.LookAheadTime)*PN.LookAheadTime, tvel);
				var lat_dist = lat_dir.magnitude;
				FormationBreakTimer.RunIf(() => keep_formation = false, 
				                          tvel_m < PN.FormationSpeedCutoff);
				VSL.Maneuvering = CanManeuver && lat_dist > CFG.Target.Distance && distance < CFG.Target.Distance*3;
				if(keep_formation && tvel_m > 0 &&
				   (!CanManeuver || 
				    dir2vel_cos <= PN.BearingCutoffCos || 
				    lat_dist < CFG.Target.Distance*3))
				{
					if(CanManeuver) VSL.CourseCorrections.Add( 
						lat_dir.normalized*Utils.ClampH(lat_dist/CFG.Target.Distance, 1) * 
						tvel_m*PN.FormationFactor*(VSL.Maneuvering? 1 : 0.5f));
					distance = Utils.ClampL(Mathf.Abs(dir2vel_cos)*distance-VSL.R, 0);
					if(dir2vel_cos < 0)
					{
						if(distance < CFG.Target.Distance)
							VSL.CourseCorrections.Add(tvel*Utils.Clamp(-distance/CFG.Target.Distance*PN.FormationFactor, -PN.FormationFactor, 0));
						else VSL.CourseCorrections.Add(tvel*(PN.FormationFactor-1));
						distance = 0;
					}
					vdir = tvel;
				}
			}
			//if the distance is greater that the threshold (in radians), use Great Circle navigation
			if(distance/VSL.mainBody.Radius > PN.DirectNavThreshold)
			{
				var next = CFG.Target.PointFrom(VSL.vessel, 0.1);
				distance = (float)CFG.Target.DistanceTo(VSL.vessel);
				vdir = Vector3.ProjectOnPlane(VSL.mainBody.GetWorldSurfacePosition(next.Lat, next.Lon, VSL.vessel.altitude)
				                              -VSL.vessel.transform.position, VSL.Up);
				tvel = Vector3.zero;
			}
			else if(!IsActiveVessel && distance > GLB.UnpackDistance) 
				VSL.SetUnpackDistance(distance*1.2f);
			vdir.Normalize();
			//check if we have arrived to the target and stayed long enough
			if(distance < end_distance)
			{
				CFG.HF.OnIfNot(VSL.NeededHorVelocity.sqrMagnitude > 1? HFlight.NoseOnCourse : HFlight.Move);
				if(CFG.Nav[Navigation.FollowTarget])
				{
					if(tvel.sqrMagnitude > 1)
					{
						//set needed velocity and starboard to match that of the target
						keep_formation = true;
						VSL.NeededHorVelocity = tvel;
						VSL.CourseCorrections.Add((tvel-VSL.HorizontalVelocity)*0.9f);
						CFG.Starboard = VSL.GetStarboard(VSL.NeededHorVelocity);
					}
					else VSL.NeededHorVelocity = CFG.Starboard = Vector3d.zero;
					vel_is_set = true;
				}
				else if(CFG.Nav[Navigation.FollowPath] && CFG.Waypoints.Count > 0)
				{
					if(CFG.Waypoints.Peek() == CFG.Target)
					{
						if(CFG.Waypoints.Count > 1)
						{ 
							CFG.Waypoints.Dequeue();
							if(on_arrival()) return;
							start_to(CFG.Waypoints.Peek());
							return;
						}
						else if(ArrivedTimer.Check)
						{ 
							CFG.Waypoints.Clear();
							if(on_arrival()) return;
							finish();
							return;
						}
					}
					else 
					{ 
						if(on_arrival()) return;
						start_to(CFG.Waypoints.Peek()); 
						return; 
					}
				}
				else if(ArrivedTimer.Check) { if(on_arrival()) return; finish(); return; }
			}
			else 
			{
				CFG.HF.OnIfNot(HFlight.NoseOnCourse);
				ArrivedTimer.Reset();
			}
			//if we need to make a sharp turn, stop and turn, then go on
			if(Vector3.Dot(vdir, VSL.Fwd) < PN.BearingCutoffCos &&
			   Vector3d.Dot(VSL.HorizontalVelocity.normalized, vdir) < PN.BearingCutoffCos)
			{
				VSL.NeededHorVelocity = vdir;
				CFG.Starboard = VSL.GetStarboard(VSL.NeededHorVelocity);
				CFG.HF.OnIfNot(HFlight.NoseOnCourse);
				VSL.Maneuvering = false;
				vel_is_set = true;
			}
			if(vel_is_set) return;
			//don't slow down on intermediate waypoints too much
			if(!CFG.Target.Land && CFG.Nav[Navigation.FollowPath] && 
			   CFG.Waypoints.Count > 1 && distance < PN.OnPathMinDistance)
			{
				WayPoint next_wp = null;
				if(CFG.Waypoints.Peek() == CFG.Target)
				{
					var iwp = CFG.Waypoints.GetEnumerator();
					try 
					{ 
						iwp.MoveNext(); iwp.MoveNext();
						next_wp = iwp.Current;
					} 
					catch {}
				}
				else next_wp = CFG.Waypoints.Peek();
				if(next_wp != null)
				{
					next_wp.Update(VSL.mainBody);
					var next_dist = Vector3.ProjectOnPlane(next_wp.GetTransform().position-CFG.Target.GetTransform().position, VSL.Up);
					var angle2next = Vector3.Angle(vdir, next_dist);
					var minD = PN.OnPathMinDistance*(1-angle2next/190);
					if(minD > distance) distance = minD;
				}
				else distance = PN.OnPathMinDistance;
			}
			//tune maximum speed
			var cur_vel = (float)Vector3d.Dot(dvel, vdir);
			pid.Min = 0;
			pid.Max = CFG.MaxNavSpeed;
			if(cur_vel > 0)
			{
				var lateral_thrust = VSL.ManualThrustLimits.Project(VSL.refT.InverseTransformDirection(vdir)).magnitude;
				var down_thrust = VSL.MaxThrust.magnitude*VSL.MinVSFtwr*0.707f;
				var brake_thrust = lateral_thrust >= down_thrust? lateral_thrust : down_thrust;
				var eta = distance/cur_vel;
				var max_speed = 0f;
				if(brake_thrust > 0)
				{
					var brake_time = cur_vel/brake_thrust*VSL.M;
					max_speed = brake_thrust/VSL.M*eta;
					if(eta <= brake_time*PN.BrakeOffset)
						VSL.CourseCorrections.Add((eta/brake_time/PN.BrakeOffset-1)*VSL.HorizontalVelocity);
				}
				if(max_speed < CFG.MaxNavSpeed) pid.Max = max_speed;
			}
			//update the needed velocity
			pid.Update(distance*PN.DistanceF);
			//increase the needed velocity slowly
			cur_vel = Utils.ClampL(cur_vel, 1);
			DeltaSpeed = PN.DeltaSpeed*Mathf.Pow(PN.DeltaSpeed/(cur_vel+PN.DeltaSpeed), PN.DeltaSpeedF);
			//make a correction if falling or flyin too low
			var mtwr = Utils.ClampL(VSL.MaxTWR, 0.1f);
			var V = (CFG.AltitudeAboveTerrain? VSL.RelVerticalSpeed : VSL.AbsVerticalSpeed);
			if(V < 0 && IsStateSet(TCAState.LoosingAltitude))
			{
				DeltaSpeed /= 1-Utils.ClampH(V, 0)
					/(CFG.AltitudeAboveTerrain? VSL.Altitude : 1)
					*PN.FallingSensitivity/mtwr;
				if(DeltaSpeed < 1) 
				{
					var a = Utils.ClampL(PN.FallingCorrection/mtwr/-V, PN.FallingCorrection);
					cur_vel *= (a-1+DeltaSpeed)/a;
				}
			}
			if(CFG.VF[VFlight.AltitudeControl])
			{
				var alt_error = (CFG.DesiredAltitude-VSL.Altitude);
				if(CFG.AltitudeAboveTerrain) alt_error /= VSL.Altitude;
				if(alt_error > 0) DeltaSpeed /= 1+alt_error*PN.AltErrSensitivity/mtwr;
			}
			//set needed velocity and starboard
			VSL.NeededHorVelocity = pid.Action-cur_vel > DeltaSpeed? vdir*(cur_vel+DeltaSpeed) : vdir*pid.Action;
			//correcto for Follow Target program
			if(CFG.Nav[Navigation.FollowTarget] && 
			   Vector3d.Dot(tvel, vdir) > 0)
				VSL.NeededHorVelocity += tvel;
			//set the starboard
			CFG.Starboard = VSL.GetStarboard(VSL.NeededHorVelocity);
//			CSV(dist2bound, Vector3d.Dot(dvel, vdir), DeltaSpeed, eta, brake_time, 
//			    pid.Max, pid.Action);//debug
		}

		#if DEBUG
		public void RadarBeam()
		{
			if(VSL == null || VSL.vessel == null) return;
			if(CFG.Target != null && CFG.Target.GetTransform() != null && CFG.Nav[Navigation.FollowTarget])
				GLUtils.GLLine(VSL.wCoM, CFG.Target.GetTransform().position+formation_offset,
				               VSL.Maneuvering? Color.yellow : Color.cyan);
		}

		public override void Reset()
		{
			base.Reset();
			RenderingManager.RemoveFromPostDrawQueue(1, RadarBeam);
		}
		#endif
	}

	public class FormationNode
	{
		public readonly Vessel Target;
		public readonly int Index;
		public Vector3 Offset { get; private set; }
		public Vessel Follower;

		public FormationNode(Vessel target, int i, Vector3 forward, Vector3 side, float dist)
		{
			Index = i+1;
			Target = target;
			Update(forward, side, dist);
		}

		public void Update(Vector3 forward, Vector3 side, float dist)
		{
			if(Index % 2 == 0) Offset = (forward+side)*dist*Index/2;
			else Offset = (forward-side)*dist*(Index+1)/2;
		}

		public float Distance(Vessel vsl)
		{ return (vsl.transform.position - Target.transform.position - Offset).magnitude; }

		public override string ToString()
		{
			return string.Format("[{0}]: Target {1}, Follower {2}, Offset {3}", 
			                     Index, Target.vesselName, Follower == null? "empty" : Follower.vesselName, Offset);
		}
	}
}

