using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Data.SqlClient;
using System.Configuration;
using System.Xml.Linq;
using System.Collections.Specialized;

namespace wp2k {
	class Program {
		static void Main(string[] args) {
			Dictionary<string, string> mapBlogPost = new Dictionary<string,string>() 
			{
					{"title", "BlogPostTitle"},
					{"link", "link"},
					{"pubDate", "BlogPostDate"},
					{"dc:creator", "dc:creator"},
					{"description", "BlogPostSummary"},
					{"content:encoded", "BlogPostBody"},
					{"excerpt:encoded", "BlogPostTeaser"},
					{"wp:post_id", "BlogPostID"},
					{"wp:post_date", "BlogPostDate"},
					{"wp:comment_status", "BlogPostAllowComments"}, // NOTE: WP = open/closed; Kentico = 0/1
					{"wp:status", "status"}, // Published/Not Published; 1/0
					{"category[domain=\"category\"]", "category"},
					{"category[domain=\"post_tag\"]", "tag"}
			};

			Dictionary<string, string> mapBlogComment = new Dictionary<string,string>()
			{
				{"wp:comment_id", "CommentID"},
				{"wp:comment_author", "CommentUserName"}, // CDATA
				{"wp:comment_author_email", "CommentEmail"},
				{"wp:comment_author_url", "CommentUrl"},
				{"wp:comment_date", "CommentDate"},
				{"wp:comment_content", "CommentText"}, // CDATA
				{"wp:comment_approved", "CommentApproved"}, // 0/1
				{"wp:comment_type", "CommentIsTrackBack"} // WP = ""/"pingback"; Kentico = 0/1
			};

			// Read config file.
			AppSettingsReader reader = new AppSettingsReader();
			NameValueCollection config = ConfigurationManager.AppSettings;

			// Read WPXML file.
			XDocument wpxml = XDocument.Load(config.Get("file"));
			var items = from i in wpxml.Descendants("item")
						select new BlogPost {
							BlogPostID = Int32.Parse(i.Element("wp:post_id").Value),
							BlogPostTitle = i.Element("title").Value,
							BlogPostDate = DateTime.Parse(i.Element("wp:post_date").Value),
							BlogPostSummary = i.Element("description").Value,
							BlogPostAllowComments = true,
							BlogPostBody = i.Element("content:encoded").Value,
							BlogPostTeaser = i.Element("excerpt:encoded").Value,
							BlogPostComments = (from c in i.Descendants("wp:comment")
												select new BlogPostComment { 
													CommentID = Int32.Parse(c.Element("wp:comment_id").Value),
													CommentUserName = c.Element("wp:comment_author").Value,
													CommentEmail = c.Element("wp:comment_author_email").Value,
													CommentUrl = c.Element("wp:comment_author_url").Value,
													CommentApproved = Boolean.Parse(c.Element("wp:comment_approved").Value),
													CommentDate = DateTime.Parse(c.Element("wp:comment_date").Value),
													CommentText = c.Element("wp:comment_content").Value,
													CommentIsTrackBack = Boolean.Parse(c.Element("wp:comment_type").Value)
												})
						};
			 

			// Insert data into database.

		}

	}

	public class BlogPost {
		public int BlogPostID { get; set; }
		public string BlogPostTitle { get; set; }
		public DateTime BlogPostDate { get; set; }
		public string BlogPostSummary { get; set; }
		public string BlogPostBody { get; set; }
		public string BlogPostTeaser { get; set; }
		public bool BlogPostAllowComments { get; set; }
		public BlogPostComment[] BlogPostComments { get; set; }
	}

	public class BlogPostComment {
		public int CommentID { get; set; }
		public string CommentUserName { get; set; }
		public int CommentUserID { get; set; }
		public string CommentUrl { get; set; }
		public string CommentText { get; set; }
		public int CommentApprovedByUserID { get; set; }
		public int CommentPostDocumentID { get; set; }
		public DateTime CommentDate { get; set; }
		public bool CommentIsSpam { get; set; }
		public bool CommentApproved { get; set; }
		public bool CommentIsTrackBack { get; set; }
		public string CommentEmail { get; set; }
		public string CommentInfo { get; set; }
	}
}
