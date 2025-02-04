﻿
using Renci.SshNet.Common;

namespace Renci.SshNet.IntegrationTests.OldIntegrationTests
{
    /// <summary>
    /// Implementation of the SSH File Transfer Protocol (SFTP) over SSH.
    /// </summary>
    public partial class SftpClientTest : IntegrationTestBase
    {
        [TestMethod]
        [TestCategory("Sftp")]
        public void Test_Sftp_CreateDirectory_In_Current_Location()
        {
            using (var sftp = new SftpClient(SshServerHostName, SshServerPort, User.UserName, User.Password))
            {
                sftp.Connect();

                sftp.CreateDirectory("test-in-current");

                sftp.Disconnect();
            }
        }

        [TestMethod]
        [TestCategory("Sftp")]
        public void Test_Sftp_CreateDirectory_In_Forbidden_Directory()
        {
            using (var sftp = new SftpClient(SshServerHostName, SshServerPort, AdminUser.UserName, AdminUser.Password))
            {
                sftp.Connect();

                Assert.ThrowsException<SftpPermissionDeniedException>(() => sftp.CreateDirectory("/sbin/test"));
            }
        }

        [TestMethod]
        [TestCategory("Sftp")]
        public void Test_Sftp_CreateDirectory_Invalid_Path()
        {
            using (var sftp = new SftpClient(SshServerHostName, SshServerPort, User.UserName, User.Password))
            {
                sftp.Connect();

                Assert.ThrowsException<SftpPathNotFoundException>(() => sftp.CreateDirectory("/abcdefg/abcefg"));
            }
        }

        [TestMethod]
        [TestCategory("Sftp")]
        public void Test_Sftp_CreateDirectory_Already_Exists()
        {
            using (var sftp = new SftpClient(SshServerHostName, SshServerPort, User.UserName, User.Password))
            {
                sftp.Connect();

                sftp.CreateDirectory("test");

                Assert.ThrowsException<SshException>(() => sftp.CreateDirectory("test"));
            }
        }
    }
}
