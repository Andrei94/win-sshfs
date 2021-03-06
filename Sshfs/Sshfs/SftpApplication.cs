﻿using System;
using System.Linq;
using System.Threading;

namespace Sshfs
{
	public static class SftpApplication
	{
		public static void Run(string[] args)
		{
			var drive = new SftpDrive
			{
				Name = args[0],
				Port = Convert.ToInt32(args[1]),
				MountPoint = $"/sftpg/{args[2]}/data/",
				Host = args[3],
				Letter = Utilities.GetAvailableDrives().Last(),
				Root = "/data",
				KeepAliveInterval = 30,
				ConnectionType = ConnectionType.Password,
				Username = args[4],
				Password = args[5]
			};
			do
			{
				try
				{
					drive.Mount();
				}
				catch(Exception)
				{
				}

				Thread.Sleep(TimeSpan.FromSeconds(15));
			} while(drive.Status != DriveStatus.Mounted);

			while(true)
			{
				Thread.Sleep(TimeSpan.FromMinutes(30));
				while(drive.Status != DriveStatus.Mounted)
					drive.Mount();
			}
		}
	}
}