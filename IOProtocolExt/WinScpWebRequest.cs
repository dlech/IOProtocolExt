﻿/*
  IOProtocolExt Plugin
  Copyright (C) 2011-2012 Dominik Reichl <dominik.reichl@t-online.de>

  This program is free software; you can redistribute it and/or modify
  it under the terms of the GNU General Public License as published by
  the Free Software Foundation; either version 2 of the License, or
  (at your option) any later version.

  This program is distributed in the hope that it will be useful,
  but WITHOUT ANY WARRANTY; without even the implied warranty of
  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
  GNU General Public License for more details.

  You should have received a copy of the GNU General Public License
  along with this program; if not, write to the Free Software
  Foundation, Inc., 51 Franklin St, Fifth Floor, Boston, MA  02110-1301  USA
*/

using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using System.Diagnostics;

using KeePass.App.Configuration;

using KeePassLib.Serialization;
using KeePassLib.Utility;

namespace IOProtocolExt
{
	public sealed class WinScpWebRequest : WebRequest
	{
		private enum WswrOp
		{
			Download = 0,
			Upload = 1,
			Delete = 2,
			Move = 3
		}

		private Uri m_uri;
		public override Uri RequestUri
		{
			get { return m_uri; }
		}

		private string m_strMethod = string.Empty;
		public override string Method
		{
			get { return m_strMethod; }
			set
			{
				if(value == null) throw new ArgumentNullException("value");
				m_strMethod = value;
			}
		}

		private WebHeaderCollection m_whcHeaders = new WebHeaderCollection();
		public override WebHeaderCollection Headers
		{
			get { return m_whcHeaders; }
			set
			{
				if(value == null) throw new ArgumentNullException("value");
				m_whcHeaders = value;
			}
		}

		private long m_lContentLength = 0;
		public override long ContentLength
		{
			get { return m_lContentLength; }
			set
			{
				if(value < 0) throw new ArgumentOutOfRangeException("value");
				m_lContentLength = value;
			}
		}

		private string m_strContentType = string.Empty;
		public override string ContentType
		{
			get { return m_strContentType; }
			set
			{
				if(value == null) throw new ArgumentNullException("value");
				m_strContentType = value;
			}
		}

		private ICredentials m_cred = null;
		public override ICredentials Credentials
		{
			get { return m_cred; }
			set { m_cred = value; }
		}

		private bool m_bPreAuth = true;
		public override bool PreAuthenticate
		{
			get { return m_bPreAuth; }
			set { m_bPreAuth = value; }
		}

		public WinScpWebRequest(Uri uri)
		{
			if(uri == null) throw new ArgumentNullException("uri");
			m_uri = uri;
		}

		private List<byte> m_lRequestData = new List<byte>();
		public override Stream GetRequestStream()
		{
			m_lRequestData.Clear();
			return new CopyMemoryStream(m_lRequestData);
		}

		private WebResponse m_wr = null;
		public override WebResponse GetResponse()
		{
			if(m_wr != null) return m_wr;

			string strTempFile = Path.GetTempFileName();

			NetworkCredential cred = (m_cred as NetworkCredential);
			string strUser = ((cred != null) ? cred.UserName : null);
			string strPassword = ((cred != null) ? cred.Password : null);

			string strSessionUrl = m_uri.Scheme + "://";
			if(!string.IsNullOrEmpty(strUser))
			{
				strSessionUrl += Uri.EscapeDataString(strUser);

				if(!string.IsNullOrEmpty(strPassword))
					strSessionUrl += (":" + Uri.EscapeDataString(strPassword));

				strSessionUrl += @"@";
			}
			strSessionUrl += m_uri.Host; // URI host is escaped
			if(m_uri.Port >= 0) strSessionUrl += (":" + m_uri.Port.ToString());

			string strRemoteDir = UrlUtil.GetFileDirectory(m_uri.AbsolutePath,
				false, false);
			string strRemoteFile = UrlUtil.GetFileName(m_uri.AbsolutePath);

			try
			{
				m_wr = RunOp(strTempFile, strSessionUrl, strRemoteDir,
					strRemoteFile);
			}
			catch(Exception)
			{
				if(File.Exists(strTempFile)) File.Delete(strTempFile);
				throw;
			}

			return m_wr;
		}

		private static StringBuilder InitScript()
		{
			StringBuilder sbScript = new StringBuilder();
			sbScript.AppendLine("option echo off");
			sbScript.AppendLine("option batch abort");
			sbScript.AppendLine("option confirm off");
			sbScript.AppendLine("option transfer binary");
			return sbScript;
		}

		private static string GetHostKey(string strError)
		{
			strError = strError.Replace("\r\n", "\n");
			strError = strError.Replace("\r", "\n");
			string[] vLines = strError.Split(new char[] { '\n' },
				StringSplitOptions.RemoveEmptyEntries);

			foreach(string strLineIt in vLines)
			{
				if(strLineIt.StartsWith("ssh-rsa")) return strLineIt;
				if(strLineIt.StartsWith("ssh-dss")) return strLineIt;
			}

			return null;
		}

		private WebResponse RunOp(string strTempFile, string strSessionUrl,
			string strRemoteDir, string strRemoteFile)
		{
			WswrOp wOp;
			if(m_strMethod == IOConnection.WrmDeleteFile) wOp = WswrOp.Delete;
			else if(m_strMethod == IOConnection.WrmMoveFile) wOp = WswrOp.Move;
			else if(m_lRequestData.Count > 0) wOp = WswrOp.Upload;
			else wOp = WswrOp.Download;

			if(wOp == WswrOp.Upload)
				File.WriteAllBytes(strTempFile, m_lRequestData.ToArray());

			StringBuilder sbHostKeyDetect = InitScript();
			sbHostKeyDetect.AppendLine(AddCommonOpenOptions("open " +
				strSessionUrl, strSessionUrl));
			sbHostKeyDetect.AppendLine("exit");

			string strHostKey = null;
			try { WinScpExecutor.RunScript(sbHostKeyDetect.ToString()); }
			catch(Exception exProbe)
			{
				strHostKey = GetHostKey(exProbe.Message);
				if(string.IsNullOrEmpty(strHostKey)) throw;
			}

			string strScript;
			if(wOp == WswrOp.Download) // Test file exists
			{
				strScript = BuildScript(WswrOp.Download, true, strTempFile,
					strSessionUrl, strRemoteDir, strRemoteFile, strHostKey);
				string strResult = WinScpExecutor.RunScript(strScript);

				strResult = strResult.Replace("\r\n", "\n");
				strResult = strResult.Replace("\r", "\n");
				string[] vLines = strResult.Split(new char[] { '\n' },
					StringSplitOptions.RemoveEmptyEntries);
				bool bFound = false;
				foreach(string strLine in vLines)
				{
					if((strLine == strRemoteFile) ||
						strLine.EndsWith(" " + strRemoteFile) ||
						strLine.EndsWith("\t" + strRemoteFile) ||
						strLine.EndsWith("/" + strRemoteFile))
					{
						bFound = true;
						break;
					}
				}
				if(!bFound) throw new FileNotFoundException("Remote file not found!");
			}

			strScript = BuildScript(wOp, false, strTempFile, strSessionUrl,
				strRemoteDir, strRemoteFile, strHostKey);

			StatusStateInfo st = null;
			if(wOp == WswrOp.Upload)
				st = StatusUtil.Begin("Uploading file...");

			WebResponse wr;
			try
			{
				if(wOp == WswrOp.Upload)
				{
					object[] vState = new object[4];
					vState[0] = m_uri;
					vState[1] = strScript;
					vState[2] = strTempFile;
					vState[3] = m_whcHeaders;

					m_bUploadResponseReady = false;
					m_exUpload = null;

					object objState = (object)vState;
					ThreadPool.QueueUserWorkItem(new WaitCallback(
						CreateUploadResponse), objState);

					bool bReady = false;
					while(!bReady)
					{
						lock(objState) { bReady = m_bUploadResponseReady; }

						Thread.Sleep(100);
						Application.DoEvents();
					}

					wr = m_wswrUpload;
					if(m_exUpload != null) throw m_exUpload;
				}
				else
					wr = new WinScpWebResponse(m_uri, strScript, strTempFile,
						(wOp == WswrOp.Download), m_whcHeaders);
			}
			finally
			{
				if(wOp == WswrOp.Upload) StatusUtil.End(st);
			}

			return wr;
		}

		private volatile bool m_bUploadResponseReady = false;
		private volatile WinScpWebResponse m_wswrUpload = null;
		private volatile Exception m_exUpload = null;
		private void CreateUploadResponse(object objState)
		{
			WinScpWebResponse wr = null;

			try
			{
				object[] v = (objState as object[]);
				if(v.Length != 4) { Debug.Assert(false); return; }

				Uri uri = (v[0] as Uri);
				string strScript = (v[1] as string);
				string strTempFile = (v[2] as string);
				WebHeaderCollection whc = (v[3] as WebHeaderCollection);

				wr = new WinScpWebResponse(uri, strScript, strTempFile, false, whc);
			}
			catch(Exception exUpload) { m_exUpload = exUpload; }

			lock(objState)
			{
				m_wswrUpload = wr;
				m_bUploadResponseReady = true;
			}
		}

		private string BuildScript(WswrOp wOp, bool bTestOnly, string strTempFile,
			string strSessionUrl, string strRemoteDir, string strRemoteFile,
			string strHostKey)
		{
			StringBuilder sbScript = InitScript();

			string strOpen = AddCommonOpenOptions("open " + strSessionUrl,
				strSessionUrl);
			if(!string.IsNullOrEmpty(strHostKey))
				strOpen += " -hostkey=\"" + strHostKey + "\"";

			sbScript.AppendLine(strOpen);

			if(strRemoteDir.Length > 0)
				sbScript.AppendLine("cd \"" + strRemoteDir + "\"");

			if(bTestOnly)
			{
				if(wOp == WswrOp.Download)
					sbScript.AppendLine("ls \"*" + strRemoteFile + "\"");
				else { Debug.Assert(false); }
			}
			else
			{
				if(wOp == WswrOp.Upload)
					sbScript.AppendLine("put \"" + strTempFile + "\" \"" +
						strRemoteFile + "\"");
				else if(wOp == WswrOp.Download)
					sbScript.AppendLine("get \"" + strRemoteFile + "\" \"" +
						strTempFile + "\"");
				else if(wOp == WswrOp.Delete)
					sbScript.AppendLine("rm \"" + strRemoteFile + "\"");
				else if(wOp == WswrOp.Move)
				{
					Uri uriTarget = new Uri(m_whcHeaders.Get(
						IOConnection.WrhMoveFileTo));

					sbScript.AppendLine("mv \"" + strRemoteFile + "\" .\"" +
						uriTarget.AbsolutePath + "\"");
				}
			}

			sbScript.AppendLine("exit");
			return sbScript.ToString();
		}

		private static string AddCommonOpenOptions(string strOpenCmd,
			string strSessionUrl)
		{
			if(string.IsNullOrEmpty(strOpenCmd)) return strOpenCmd;
			if(strSessionUrl == null) { Debug.Assert(false); return strOpenCmd; }

			AceCustomConfig cfg = IOProtocolExtExt.Host.CustomConfig;
			string str = strOpenCmd;

			ulong uTimeout = cfg.GetULong(IopDefs.OptTimeout, 0);
			if(uTimeout > 0) str += (" -timeout=" + uTimeout.ToString());

			if(strSessionUrl.StartsWith("ftps:", StrUtil.CaseIgnoreCmp))
			{
				if(cfg.GetBool(IopDefs.OptFtpsImplicit, false))
					str += " -implicit";
				if(cfg.GetBool(IopDefs.OptFtpsExplicitSsl, false))
					str += " -explicitssl";
				if(cfg.GetBool(IopDefs.OptFtpsExplicitTls, false))
					str += " -explicittls";
			}

			return str;
		}
	}
}
