using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Data.SqlClient;
using System.Configuration;
using System.Xml.Linq;
using System.Collections.Specialized;
using System.Transactions;
using System.Text.RegularExpressions;

namespace wp2k {
	class Program {
		protected static string forbiddenChars = "[^0-9a-zA-Z_-]"; // Anything that's not alphanumeric, underscore, or dash

		static void Main(string[] args) {
			// Read config file.
			AppSettingsReader reader = new AppSettingsReader();
			NameValueCollection config = ConfigurationManager.AppSettings;

			// Read WPXML file and get relevant namespaces.
			XDocument wpxml = XDocument.Load(config.Get("file"));
			XNamespace wpns = "http://wordpress.org/export/1.1/";
			XNamespace encoded = "http://purl.org/rss/1.0/modules/content/";

			Regex forbidden = new Regex(forbiddenChars);

			using (kenticofreeEntities context = new kenticofreeEntities()) {
				var blogId = Int32.Parse(config.Get("kenticoBlogId"));
				var siteId = Int32.Parse(config.Get("siteId"));
				int nodeOwnerId = Int32.Parse(config.Get("nodeOwnerId"));

				// Gather list of Posts and make them each Entities
				var items = (from i in wpxml.Descendants("item")
							 where i.Element(wpns + "post_type").Value == "post" && i.Element(wpns + "status").Value == "publish"
							 select new CONTENT_BlogPost {
								 BlogPostID = Int32.Parse(i.Element(wpns + "post_id").Value),
								 BlogPostTitle = i.Element("title").Value,
								 BlogPostDate = DateTime.Parse(i.Element(wpns + "post_date").Value),
								 BlogPostSummary = i.Element("description").Value,
								 BlogPostAllowComments = true,
								 BlogPostBody = i.Element(encoded + "encoded").Value, // CDATA element
								 BlogLogActivity = true
							 });

				// Get the blog by the ID set in the settings
				CMS_Tree blog = (from b in context.CONTENT_Blog
								 join d in context.CMS_Document on b.BlogID equals d.DocumentForeignKeyValue
								 join t in context.CMS_Tree on d.DocumentNodeID equals t.NodeID
									 where b.BlogID == blogId && t.NodeClassID == 3423 //Blog class ID, otherwise we end up with a bunch of stuff that aren't blogs
									 select t).First();
				
				foreach (CONTENT_BlogPost post in items) {
					/* We want to preserve the IDs of the Posts, but EF won't let us turn on IDENTITY_INSERT
					 * with its available methods (ExecuteStoreCommand and SaveChanges are different connections, it seems). 
					 * So we have to do it the old fashioned way.
					 */
					object[] values = new object[]{
							post.BlogPostID.ToString(),
							post.BlogPostTitle,
							post.BlogPostDate.Date.ToString("yyyy-MM-dd HH:mm:ss"),
							post.BlogPostSummary,
							post.BlogPostAllowComments,
							post.BlogPostBody,
							post.BlogLogActivity
						};

					/* We'll use MERGE here, so that we can handle existing entries.
					 * The "xmlTrumpsDb" config switch will allow a choice between nuking what's in the DB
					 * or preserving it.
					 */
					string cmd = "SET IDENTITY_INSERT CONTENT_BlogPost ON; ";
					cmd += "MERGE CONTENT_BlogPost ";
					cmd += "USING (VALUES ({0},{1},{2},{3},{4},{5},{6})) as temp(BlogPostID, BlogPostTitle, BlogPostDate, BlogPostSummary, BlogPostAllowComments, BlogPostBody, BlogLogActivity) ";
					cmd += "ON CONTENT_BlogPost.BlogPostID = temp.BlogPostID ";
					// To nuke or not to nuke, that is the question...
					if(config.Get("xmlTrumpsDb") == "true") {
						cmd += "WHEN MATCHED THEN ";
						cmd += "UPDATE SET CONTENT_BlogPost.BlogPostTitle = temp.BlogPostTitle, CONTENT_BlogPost.BlogPostDate = temp.BlogPostDate, CONTENT_BlogPost.BlogPostSummary = temp.BlogPostSummary, CONTENT_BlogPost.BlogPostAllowComments = temp.BlogPostAllowComments, CONTENT_BlogPost.BlogPostBody = temp.BlogPostBody, CONTENT_BlogPost.BlogLogActivity = temp.BlogLogActivity ";
					}
					cmd += "WHEN NOT MATCHED THEN ";
					cmd += "INSERT (BlogPostId, BlogPostTitle, BlogPostDate, BlogPostSummary, BlogPostAllowComments, BlogPostBody, BlogLogActivity) VALUES ({0},{1},{2},{3},{4},{5},{6}); ";
					cmd += "SET IDENTITY_INSERT CONTENT_BlogPost OFF;";
					context.ExecuteStoreCommand(cmd, values);

					// See if there's a BlogMonth entry for the month this post is for
					CMS_Tree month = GetBlogMonth(context, post, blog, config);

					CMS_Class blogClass = context.CMS_Class.Where(x => x.ClassName == "CMS.BlogPost").First();

					CMS_Tree treeNode = (from t in context.CMS_Tree
										 join d in context.CMS_Document on t.NodeID equals d.DocumentNodeID
										 where d.DocumentForeignKeyValue == post.BlogPostID && t.NodeClassID == 3423
										 select t).FirstOrDefault();

					// Add a new node only if one doesn't already exist
					if (treeNode == null) {
						// Create the Tree Node for the post and add it in
						treeNode = new CMS_Tree() {
							NodeAliasPath = string.Format("{0}/{1}", month.NodeAliasPath, forbidden.Replace(post.BlogPostTitle, "-")),
							NodeName = post.BlogPostTitle,
							NodeAlias = forbidden.Replace(post.BlogPostTitle, "-"),
							NodeClassID = blogClass.ClassID,
							NodeParentID = month.NodeID,
							NodeLevel = Int32.Parse(config.Get("kenticoBlogLevel"))+2,
							NodeACLID = 1, // Default ACL ID
							NodeSiteID = siteId,
							NodeGUID = Guid.NewGuid(),
							NodeOwner = nodeOwnerId,
							NodeInheritPageLevels = "",
							NodeTemplateForAllCultures = true
						};
						context.CMS_Tree.AddObject(treeNode);
						treeNode.NodeOrder = GetNodeOrder(context, treeNode);
						month.NodeChildNodesCount++; // Increment the child nodes count, so the new post will display in the CMS
						context.SaveChanges();
					}

					// Create the document and add it into the database
					CMS_Document postDoc = AddDocument(context, treeNode, post.BlogPostID);

					// Get the comments from the XML
					//var comments = (from c in wpxml.Descendants("item").Where(x => x.Element(wpns + "post_id").Value == post.BlogPostID.ToString()).Descendants(wpns + "comment")
					//                select new Blog_Comment {
					//                    CommentID = Int32.Parse(c.Element(wpns + "comment_id").Value),
					//                    CommentUserName = c.Element(wpns + "comment_author").Value,
					//                    CommentEmail = c.Element(wpns + "comment_author_email").Value,
					//                    CommentUrl = c.Element(wpns + "comment_author_url").Value,
					//                    CommentApproved = (c.Element(wpns + "comment_approved").Value != "0"), // Convert "0"/"1" to false/true
					//                    CommentDate = DateTime.Parse(c.Element(wpns + "comment_date").Value),
					//                    CommentText = c.Element(wpns + "comment_content").Value,
					//                    CommentIsTrackBack = !String.IsNullOrEmpty(c.Element(wpns + "comment_type").Value),
					//                    CommentPostDocumentID = postDoc.DocumentID
					//                }
					//);

					//// Insert them into the database
					//foreach (Blog_Comment comment in comments) {
					//    context.Blog_Comment.AddObject(comment);
					//}

					context.SaveChanges();

					// Break for now, so we don't have three years' worth of entries on the trial runs
					break;
				}
			}
		}

		protected static CMS_Tree GetBlogMonth(kenticofreeEntities context, CONTENT_BlogPost post, CMS_Tree blog, NameValueCollection config) {
			Regex forbidden = new Regex(forbiddenChars);
			var monthQuery = (from m in context.CONTENT_BlogMonth
							  where m.BlogMonthStartingDate.Year == post.BlogPostDate.Year && m.BlogMonthStartingDate.Month == post.BlogPostDate.Month
							  select m);

			CMS_Tree treeNode = new CMS_Tree();

			// If not, make a new one
			if (monthQuery.Any() == false) {
				CONTENT_BlogMonth month = new CONTENT_BlogMonth() {
					BlogMonthName = post.BlogPostDate.ToString("MMMM yyyy"),
					BlogMonthStartingDate = new DateTime(post.BlogPostDate.Year, post.BlogPostDate.Month, 01)
				};

				context.CONTENT_BlogMonth.AddObject(month);

				CMS_Class monthClass = context.CMS_Class.Where(x => x.ClassName == "CMS.BlogMonth").First();
				
				// Add the corresponding tree node
				treeNode = new CMS_Tree() {
					NodeAliasPath = string.Format("{0}/{1}/{2}", config.Get("kenticoBlogPath"), forbidden.Replace(blog.NodeName, "-"), forbidden.Replace(month.BlogMonthName, "-")),
					NodeTemplateForAllCultures = true,
					NodeName = month.BlogMonthName,
					NodeAlias = forbidden.Replace(month.BlogMonthName, "-"),
					NodeClassID = monthClass.ClassID,
					NodeParentID = blog.NodeID,
					NodeLevel = Int32.Parse(config.Get("kenticoBlogLevel")) + 1,
					NodeACLID = 1, // Default ACL ID
					NodeSiteID = Int32.Parse(config.Get("siteId")),
					NodeGUID = Guid.NewGuid(),
					NodeOwner = Int32.Parse(config.Get("nodeOwnerId")),
					NodeInheritPageLevels = "",
					NodeChildNodesCount = 0 // Start out with a number, instead of NULL, so we can easily increment it
				};

				treeNode.NodeOrder = GetNodeOrder(context, treeNode);

				context.CMS_Tree.AddObject(treeNode);
				context.SaveChanges();

				AddDocument(context, treeNode, month.BlogMonthID);
			}
			else {
				CONTENT_BlogMonth month = monthQuery.First();
				treeNode = (from t in context.CMS_Tree
							join d in context.CMS_Document on t.NodeID equals d.DocumentNodeID
							join b in context.CONTENT_BlogMonth on d.DocumentForeignKeyValue equals b.BlogMonthID
								where b.BlogMonthID == month.BlogMonthID
								select t).Single();
			}

			return treeNode;
		}

		protected static CMS_Document AddDocument(kenticofreeEntities context, CMS_Tree treeNode, int itemId) {
					// Create the document and add it into the database
					CMS_Document doc = new CMS_Document() {
						DocumentName = treeNode.NodeName,
						DocumentNamePath = treeNode.NodeAliasPath,
						DocumentForeignKeyValue = itemId,
						DocumentCulture = "en-US",
						DocumentShowInSiteMap = true,
						DocumentMenuItemHideInNavigation = false,
						DocumentUseNamePathForUrlPath = false,
						DocumentStylesheetID = -1,
						DocumentNodeID = treeNode.NodeID,
						DocumentGUID = Guid.NewGuid(),
						DocumentWorkflowCycleGUID = Guid.NewGuid(),
						DocumentCreatedByUserID = treeNode.NodeOwner,
						DocumentCreatedWhen = DateTime.Now,
						DocumentModifiedWhen = DateTime.Now,
						DocumentModifiedByUserID = treeNode.NodeOwner,
						DocumentUrlPath = "",
						DocumentContent = "",
						DocumentIsArchived = false
					};

					context.CMS_Document.AddObject(doc);
					context.SaveChanges();

					return doc;
		}

		protected static int GetNodeOrder(kenticofreeEntities context, CMS_Tree tree) {
			int nodeOrder = 1;
			CMS_Tree lastNode = (from node in context.CMS_Tree
								 where node.NodeParentID == tree.NodeParentID
								 orderby node.NodeOrder descending
								 select node).FirstOrDefault();

			if (lastNode != null) {
				nodeOrder = (int)lastNode.NodeOrder;
			}

			return nodeOrder;
		}
	}
}