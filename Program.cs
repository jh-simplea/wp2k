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
			// TODO: God functions are evil.

			// Read config file.
			AppSettingsReader reader = new AppSettingsReader();
			NameValueCollection config = ConfigurationManager.AppSettings;

			// Read WPXML file.
			XDocument wpxml = XDocument.Load(config.Get("file"));

			using(kenticofreeEntities context = new kenticofreeEntities()) {

				// Gather list of Posts and make them each Entities
				var items = (from i in wpxml.Descendants("item")
						 select new CONTENT_BlogPost {
							 BlogPostID = Int32.Parse(i.Element("wp:post_id").Value),
							 BlogPostTitle = i.Element("title").Value,
							 BlogPostDate = DateTime.Parse(i.Element("wp:post_date").Value),
							 BlogPostSummary = i.Element("description").Value,
							 BlogPostAllowComments = true,
							 BlogPostBody = i.Element("content:encoded").Value,
							 BlogLogActivity = true
							 //BlogPostTeaser = i.Element("excerpt:encoded").Value
						 });
						//.Union(from c in i.Descendants("wp:comment")
						//       select new Blog_Comment {
						//           CommentID = Int32.Parse(c.Element("wp:comment_id").Value),
						//           CommentUserName = c.Element("wp:comment_author").Value,
						//           CommentEmail = c.Element("wp:comment_author_email").Value,
						//           CommentUrl = c.Element("wp:comment_author_url").Value,
						//           CommentApproved = Boolean.Parse(c.Element("wp:comment_approved").Value),
						//           CommentDate = DateTime.Parse(c.Element("wp:comment_date").Value),
						//           CommentText = c.Element("wp:comment_content").Value,
						//           CommentIsTrackBack = Boolean.Parse(c.Element("wp:comment_type").Value)
						//       }
						//);

				// Get the blog by the ID set in the settings
				CONTENT_Blog blog = (from b in context.CONTENT_Blog
									 where b.BlogID == Int32.Parse(config.Get("kenticoBlogId"))
									 select b).First();

				foreach (CONTENT_BlogPost post in items) {
					// Insert the post into the DB
					// Why the hell isn't there a bulk update for collections/list of entities?
					context.CONTENT_BlogPost.AddObject(post);
					context.SaveChanges();

					// See if there's a BlogMonth entry for the month this post is for
					var monthQuery = (from m in context.CONTENT_BlogMonth
											   where m.BlogMonthStartingDate.Year == post.BlogPostDate.Year && m.BlogMonthStartingDate.Month == post.BlogPostDate.Month
											   select m);
					CONTENT_BlogMonth month = new CONTENT_BlogMonth();
					
					// If not, make a new one
					if (monthQuery.Any() == false) {
						month.BlogMonthName = post.BlogPostDate.ToString("MMMM yyyy");
						month.BlogMonthStartingDate = new DateTime(post.BlogPostDate.Year, post.BlogPostDate.Month, 01);

						context.CONTENT_BlogMonth.AddObject(month);
						context.SaveChanges();
					}
					else {
						month = monthQuery.First();
					}
					
					CMS_Document doc = new CMS_Document() {
						DocumentName = post.BlogPostTitle,
						DocumentNamePath = string.Format("/{0}/{1}/{2}", blog.BlogName, month.BlogMonthName, post.BlogPostTitle),
						DocumentForeignKeyValue = post.BlogPostID,
						DocumentCulture = "en-US",
						DocumentShowInSiteMap = true,
						DocumentMenuItemHideInNavigation = false,
						DocumentUseNamePathForUrlPath = false,
						DocumentStylesheetID = -1
					};
				}
				// Iterate through our Post entities and find each one's corresponding comments

				// Turn the comments in a list of Entities and use the documentID from insert		
			}
		}
	}
}