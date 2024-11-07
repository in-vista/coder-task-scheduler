using System;
using System.ComponentModel;
using System.Xml;
using System.Xml.Serialization;
using WiserTaskScheduler.Core.Enums;
using WiserTaskScheduler.Modules.HttpApis.Models;

namespace WiserTaskScheduler.Core.Models.OAuth
{
    [XmlType("OAuth")]
    public class OAuthModel
    {
        /// <summary>
        /// Gets or sets the name of the API which is used to target it in an HTTP API request.
        /// </summary>
        public string ApiName { get; set; }

        /// <summary>
        /// Gets or sets the URL to the endpoint where the OAuth needs to be done.
        /// </summary>
        public string Endpoint { get; set; }
        
        /// <summary>
        /// Gets or sets the GrantType used to receive the token
        /// Currently only PasswordCredentials and ClientCredentials are supported
        /// </summary>
        public OAuthGrantType GrantType { get; set; } = OAuthGrantType.PasswordCredentials;
        
        /// <summary>
        /// When using GrantType = ClientCredentials, either send the data in the body or as auth basic header(default)
        /// </summary>
        public bool SendClientCredentialsInBody { get; set; } = false;
        
        /// <summary>
        /// Gets or sets the username to login with when no access token or refresh token is available.
        /// This field is required when using PasswordCredentials type
        /// </summary>
        public string Username { get; set; }

        /// <summary>
        /// Gets or sets the password to login with when no access token or refresh token is available.
        /// /// This field is required when using PasswordCredentials type
        /// </summary>
        public string Password { get; set; }
        
        /// <summary>
        /// Gets or sets the Client Id to login with when no access token or refresh token is available.
        /// This field is required when using ClientCredentials type
        /// </summary>
        public string ClientId { get; set; }

        /// <summary>
        /// Gets or sets the Client Secret to login with when no access token or refresh token is available.
        /// /// This field is required when using ClientCredentials type
        /// </summary>
        public string ClientSecret { get; set; }

        /// <summary>
        /// Gets or sets the offset from the expire time.
        /// </summary>
        [XmlIgnore]
        public TimeSpan ExpireTimeOffset { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Gets or sets <see cref="ExpireTimeOffset"/> from a XML file.
        /// </summary>
        [Browsable(false)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        [XmlElement("ExpireTimeOffset")]
        public string DelayString
        {
            get => XmlConvert.ToString(ExpireTimeOffset);
            set => ExpireTimeOffset = String.IsNullOrWhiteSpace(value) ? TimeSpan.Zero : value.StartsWith("P") ? XmlConvert.ToTimeSpan(value) : TimeSpan.Parse(value);
        }

        /// <summary>
        /// Gets or sets the log settings.
        /// </summary>
        public LogSettings LogSettings { get; set; }

        /// <summary>
        /// Gets or sets custom key value pairs to include in the form.
        /// </summary>
        [XmlArray("FormKeyValues")]
        [XmlArrayItem(typeof(FormKeyValueModel))]
        public FormKeyValueModel[] FormKeyValues { get; set; }
        
        /// <summary>
        /// Gets or sets custom headers to include in the oauth request.
        /// </summary>
        [XmlArray("Headers")]
        [XmlArrayItem(typeof(HeaderModel))]
        public HeaderModel[] Headers { get; set; }

        /// <summary>
        /// Gets or sets the JWT settings for the OAuth payload.
        /// </summary>
        [XmlElement("Jwt")]
        public OAuthJwtModel OAuthJwt { get; set; }

        /// <summary>
        /// Gets or sets the access token.
        /// </summary>
        [XmlIgnore]
        public string AccessToken { get; set; }

        /// <summary>
        /// Gets or sets the type of the token.
        /// </summary>
        [XmlIgnore]
        public string TokenType { get; set; }

        /// <summary>
        /// Gets or sets the refresh token.
        /// </summary>
        public string RefreshToken { get; set; }

        /// <summary>
        /// Gets or sets the time the access token expires on.
        /// </summary>
        [XmlIgnore]
        public DateTime ExpireTime { get; set; }
    }
}
